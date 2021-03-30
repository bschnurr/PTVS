﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Core.Disposables;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.LanguageServerClient.StreamHacking;
using Microsoft.PythonTools.LanguageServerClient.WorkspaceFolderChanged;
using Microsoft.PythonTools.Options;
using Microsoft.PythonTools.Project;
using Microsoft.PythonTools.Utility;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.PythonTools.LanguageServerClient {
    /// <summary>
    /// Implementation of the language server client.
    /// </summary>
    /// <remarks>
    /// See documentation at https://docs.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension?view=vs-2019
    /// </remarks>
    [Export(typeof(ILanguageClient))]
    [ContentType(PythonCoreConstants.ContentType)]
    internal sealed class PythonLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable {
        [Import(typeof(SVsServiceProvider))]
        public IServiceProvider Site;

        [Import]
        public IContentTypeRegistryService ContentTypeRegistryService;

        [Import]
        public IPythonWorkspaceContextProvider PythonWorkspaceContextProvider;

        [Import]
        public VsProjectContextProvider ProjectContextProvider;

        [Import]
        public IInterpreterOptionsService OptionsService;

        [Import]
        public JoinableTaskContext JoinableTaskContext;

        private readonly DisposableBag _disposables;
        private IPythonLanguageClientContext[] _clientContexts;
        private PythonAnalysisOptions _analysisOptions;
        private PythonAdvancedEditorOptions _advancedEditorOptions;
        private LanguageServer _server;
        private JsonRpc _rpc;
        private bool _workspaceFoldersSupported = false;
        private bool _isDebugging = LanguageServer.IsDebugging();
        private bool _sentInitialWorkspaceFolders = false;

        public PythonLanguageClient() {
            _disposables = new DisposableBag(GetType().Name);
        }

        public string ContentTypeName => PythonCoreConstants.ContentType;

        public string Name => @"Pylance";

        // Called by Microsoft.VisualStudio.LanguageServer.Client.RemoteLanguageServiceBroker.UpdateClientsWithConfigurationSettingsAsync
        // Used to send LS WorkspaceDidChangeConfiguration notification
        public IEnumerable<string> ConfigurationSections => Enumerable.Repeat("python", 1);
        public object InitializationOptions { get; private set; }

        // TODO: investigate how this can be used, VS does not allow currently
        // for the server to dynamically register file watching.
        public IEnumerable<string> FilesToWatch => null;
        public object MiddleLayer => null;
        public object CustomMessageTarget { get; private set; }
        public bool IsInitialized { get; private set; }

        public event AsyncEventHandler<EventArgs> StartAsync;
#pragma warning disable CS0067
        public event AsyncEventHandler<EventArgs> StopAsync;
#pragma warning restore CS0067

        public async Task<Connection> ActivateAsync(CancellationToken token) {
            if (_server == null) {
                Debug.Fail("Should not have called StartAsync when _server is null.");
                return null;
            }
            await JoinableTaskContext.Factory.SwitchToMainThreadAsync();

            _clientContexts = CreateClientContexts();

            _analysisOptions = Site.GetPythonToolsService().AnalysisOptions;
            _advancedEditorOptions = Site.GetPythonToolsService().AdvancedEditorOptions;

            Array.ForEach(_clientContexts, c => c.InterpreterChanged += OnSettingsChanged);
            _analysisOptions.Changed += OnSettingsChanged;
            _advancedEditorOptions.Changed += OnSettingsChanged;
            var dte = (EnvDTE80.DTE2)Site.GetService(typeof(EnvDTE.DTE));
            var solutionEvents = dte.Events.SolutionEvents;
            solutionEvents.Opened += OnSolutionOpened;
            solutionEvents.BeforeClosing += OnSolutionClosing;
            solutionEvents.ProjectAdded += OnProjectAdded;
            solutionEvents.ProjectRemoved += OnProjectRemoved;

            _disposables.Add(() => {
                Array.ForEach(_clientContexts, c => c.InterpreterChanged -= OnSettingsChanged);
                _analysisOptions.Changed -= OnSettingsChanged;
                _advancedEditorOptions.Changed -= OnSettingsChanged;
                Array.ForEach(_clientContexts, c => c.Dispose());
                solutionEvents.Opened -= OnSolutionOpened;
                solutionEvents.ProjectAdded -= OnProjectAdded;
                solutionEvents.ProjectRemoved -= OnProjectRemoved;
                solutionEvents.BeforeClosing -= OnSolutionClosing;
            });

            return await _server.ActivateAsync();
        }

        public async Task OnLoadedAsync() {
            await JoinableTaskContext.Factory.SwitchToMainThreadAsync();
            // Force the package to load, since this is a MEF component,
            // there is no guarantee it has been loaded.
            Site.GetPythonToolsService();

            // Client context cannot be created here since the is no workspace yet
            // and hence we don't know if this is workspace or a loose files case.
            _server = new LanguageServer(Site, JoinableTaskContext, this.OnSendToServer);
            InitializationOptions = null;
            CustomMessageTarget = new PythonLanguageClientCustomTarget(Site, JoinableTaskContext, OnWorkspaceFolderWatched);
            await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task OnServerInitializedAsync() {
            IsInitialized = true;
            OnSolutionOpened();
        }

        public Task OnServerInitializeFailedAsync(Exception e) {
            MessageBox.ShowErrorMessage(Site, Strings.LanguageClientInitializeFailed.FormatUI(e));
            return Task.CompletedTask;
        }

        public Task AttachForCustomMessageAsync(JsonRpc rpc) {
            _rpc = rpc;

            // This is a workaround until we have proper API from ILanguageClient for now.
            _rpc.AllowModificationWhileListening = true;
            _rpc.CancellationStrategy = new CustomCancellationStrategy(_server.CancellationFolderName, _rpc);
            _rpc.AllowModificationWhileListening = false;

            // We also need to switch the order on handlers for all of the rpc targets. Until
            // the VSSDK gives us a way to do this, use reflection.
            try {
                var fi = rpc.GetType().GetField("rpcTargetInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fi != null) {
                    var rpcTargetInfo = fi.GetValue(rpc);
                    if (rpcTargetInfo != null) {
                        fi = rpcTargetInfo.GetType().GetField("targetRequestMethodToClrMethodMap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (fi != null) {
                            // Have to use reflection all the way down as the type of the entry is private
                            var dictionary = fi.GetValue(rpcTargetInfo);
                            var method = dictionary?.GetType().GetMethod("get_Keys");
                            var keys = method?.Invoke(dictionary, new object[0]) as IEnumerable<string>;
                            foreach (var key in keys) {
                                method = dictionary?.GetType().GetMethod("get_Item");
                                var list = method?.Invoke(dictionary, new object[1] { key });
                                var reverse = list?.GetType().GetMethod(
                                    "Reverse",
                                    BindingFlags.Public | BindingFlags.Instance,
                                    null,
                                    CallingConventions.Any,
                                    new Type[] { },
                                    null);
                                reverse?.Invoke(list, new object[0]);
                            }
                        }
                    }
                }
            } catch {
                // Any exceptions, just skip this part
            }

            return Task.CompletedTask;
        }

        public void Dispose() => _disposables.TryDispose();

        public Task InvokeTextDocumentDidOpenAsync(LSP.DidOpenTextDocumentParams request)
            => _rpc == null ? Task.CompletedTask : _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", request);

        public Task InvokeTextDocumentDidChangeAsync(LSP.DidChangeTextDocumentParams request)
            => _rpc == null ? Task.CompletedTask : _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", request);

        public Task InvokeDidChangeConfigurationAsync(LSP.DidChangeConfigurationParams request)
            => _rpc == null ? Task.CompletedTask : _rpc.NotifyWithParameterObjectAsync("workspace/didChangeConfiguration", request);

        public Task InvokeDidChangeWorkspaceFoldersAsync(WorkspaceFolder[] added, WorkspaceFolder[] removed) => 
            _rpc == null ? Task.CompletedTask : _rpc.NotifyWithParameterObjectAsync("workspace/didChangeWorkspaceFolders",
                    new DidChangeWorkspaceFoldersParams { changeEvent = new WorkspaceFoldersChangeEvent { added = added, removed = removed } });

        public Task<LSP.CompletionList> InvokeTextDocumentCompletionAsync(LSP.CompletionParams request, CancellationToken cancellationToken = default)
            => _rpc == null ? Task.FromResult(new LSP.CompletionList()) : _rpc.InvokeWithParameterObjectAsync<LSP.CompletionList>("textDocument/completion", request, cancellationToken);

        public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object argument = null, CancellationToken cancellationToken = default)
            => _rpc == null ? Task.FromResult(default(TResult)) : _rpc.InvokeWithParameterObjectAsync<TResult>(targetName, argument, cancellationToken);

        private void OnSettingsChanged(object sender, EventArgs e) => SendDidChangeConfigurations().DoNotWait();

        private Task SendDidChangeConfigurations() {
            return Task.WhenAll(_clientContexts.Select(c => SendDidChangeConfiguration(c)));
        }

        private async Task SendDidChangeConfiguration(IPythonLanguageClientContext context) {
            if (context is PythonLanguageClientContextProject) {
                // Project interactions are main thread only.
                await JoinableTaskContext.Factory.SwitchToMainThreadAsync();
            }

            Debug.Assert(context != null);
            Debug.Assert(_analysisOptions != null);

            var extraPaths = UserSettings.GetStringSetting(
                PythonConstants.ExtraPathsSetting, null, Site, PythonWorkspaceContextProvider.Workspace, out _)?.Split(';')
                ?? _analysisOptions.ExtraPaths;

            var stubPath = UserSettings.GetStringSetting(
                PythonConstants.StubPathSetting, null, Site, PythonWorkspaceContextProvider.Workspace, out _)
                ?? _analysisOptions.StubPath;

            var typeCheckingMode = UserSettings.GetStringSetting(
                PythonConstants.TypeCheckingModeSetting, null, Site, PythonWorkspaceContextProvider.Workspace, out _)
                ?? _analysisOptions.TypeCheckingMode;

            var ver3 = new Version(3, 0);
            if (context.InterpreterConfiguration.Version < ver3) {
                MessageBox.ShowWarningMessage(Site, Strings.WarningPython2NotSupported);
            }

            var settings = new LanguageServerSettings {
                python = new LanguageServerSettings.PythonSettings {
                    pythonPath = context.InterpreterConfiguration.InterpreterPath,
                    venvPath = string.Empty,
                    analysis = new LanguageServerSettings.PythonSettings.PythonAnalysisSettings {
                        logLevel = _analysisOptions.LogLevel,
                        autoSearchPaths = _analysisOptions.AutoSearchPaths,
                        diagnosticMode = _analysisOptions.DiagnosticMode,
                        extraPaths = extraPaths,
                        stubPath = stubPath,
                        typeshedPaths = _analysisOptions.TypeshedPaths,
                        typeCheckingMode = typeCheckingMode,
                        useLibraryCodeForTypes = true,
                        completeFunctionParens = _advancedEditorOptions.CompleteFunctionParens,
                        autoImportCompletions = _advancedEditorOptions.AutoImportCompletions
                    }
                }
            };

            var config = new LSP.DidChangeConfigurationParams() {
                Settings = settings
            };
            await InvokeDidChangeConfigurationAsync(config);
        }

        private void OnWorkspaceFolderWatched() {
            _workspaceFoldersSupported = true;
            OnSolutionOpened();
        }

        private IPythonLanguageClientContext[] CreateClientContexts() {
            if (PythonWorkspaceContextProvider.Workspace != null) {
                return new IPythonLanguageClientContext[] { new PythonLanguageClientContextWorkspace(PythonWorkspaceContextProvider.Workspace) };
            }

            if (ProjectContextProvider.ProjectNodes.MaybeEnumerate().Any()) {
                var nodes = from n in ProjectContextProvider.ProjectNodes
                            select new PythonLanguageClientContextProject(n);
                return nodes.ToArray();
            }

            return new IPythonLanguageClientContext[] { new PythonLanguageClientContextGlobal(OptionsService) };
        }

        // This is all a hack until VSSDK LanguageServer can handle workspace folders
        private StreamData OnSendToServer(StreamData data) {
            var message = MessageParser.Deserialize(data);
            if (message != null) {
                try {
                    // If this is the initialize method, add the workspace folders capability
                    if (message.Value<string>("method") == "initialize") {
                        if (message.TryGetValue("params", out JToken messageParams)) {
                            var capabilities = messageParams["capabilities"];
                            if (capabilities != null) {
                                if (capabilities["workspace"] == null) {
                                    capabilities["workspace"] = JToken.FromObject(new { });
                                }
                                capabilities["workspace"]["workspaceFolders"] = true;

                                // Need to rewrite the message now
                                return MessageParser.Serialize(message);
                            }
                        }
                    }
                } catch {
                    // Don't care if this happens. Just skip the message
                }
            }
            return data;
        }

        private void OnSolutionClosing() {
            if (_workspaceFoldersSupported && IsInitialized && _sentInitialWorkspaceFolders) {
                JoinableTaskContext.Factory.RunAsync(async () => {
                    // If workspace folders are supported, then send our workspace folders
                    var folders = from n in this.ProjectContextProvider.ProjectNodes
                                  select new WorkspaceFolder { uri = new System.Uri(n.BaseURI.Directory), name = n.Name };
                    if (folders.Any()) {
                        await InvokeDidChangeWorkspaceFoldersAsync(new WorkspaceFolder[0], folders.ToArray());
                    }
                });
                _workspaceFoldersSupported = false;
                IsInitialized = false;
                _sentInitialWorkspaceFolders = false;
            }
        }

        private void OnSolutionOpened() {
            if (_workspaceFoldersSupported && IsInitialized && !_sentInitialWorkspaceFolders) {
                _sentInitialWorkspaceFolders = true;
                JoinableTaskContext.Factory.RunAsync(async () => {
                    // If workspace folders are supported, then send our workspace folders
                    var folders = from n in this.ProjectContextProvider.ProjectNodes
                                  select new WorkspaceFolder { uri = new System.Uri(n.BaseURI.Directory), name = n.Name };
                    if (folders.Any()) {
                        await InvokeDidChangeWorkspaceFoldersAsync(folders.ToArray(), new WorkspaceFolder[0]);
                    }
                });
            }
        }

        private void OnProjectAdded(EnvDTE.Project project) {
            if (_workspaceFoldersSupported) {
                JoinableTaskContext.Factory.RunAsync(async () => {
                    var pythonProject = project as PythonProjectNode;
                    if (pythonProject != null) {
                        var folder = new WorkspaceFolder { uri = new System.Uri(pythonProject.BaseURI.Directory), name = project.Name };
                        await InvokeDidChangeWorkspaceFoldersAsync(new WorkspaceFolder[] { folder }, new WorkspaceFolder[0]);
                    }
                });
            }
        }

        private void OnProjectRemoved(EnvDTE.Project project) {
            if (_workspaceFoldersSupported) {
                JoinableTaskContext.Factory.RunAsync(async () => {
                    var pythonProject = project as PythonProjectNode;
                    if (pythonProject != null) {
                        var folder = new WorkspaceFolder { uri = new System.Uri(pythonProject.BaseURI.Directory), name = project.Name };
                        await InvokeDidChangeWorkspaceFoldersAsync(new WorkspaceFolder[0], new WorkspaceFolder[] { folder });
                    }
                });
            }
        }
    }
}
