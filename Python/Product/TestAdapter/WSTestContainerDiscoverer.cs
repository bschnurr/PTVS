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
using System.IO;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Projects;
using Microsoft.PythonTools.TestAdapter.Model;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.TestAdapter;
using Microsoft.PythonTools.Interpreter;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Microsoft.PythonTools.TestAdapter {
    [Export(typeof(ITestContainerDiscoverer))]
    [Export(typeof(WSTestContainerDiscoverer))]
    class WSTestContainerDiscoverer : ITestContainerDiscoverer, IDisposable {
        private readonly IServiceProvider _serviceProvider;
        private readonly IPythonWorkspaceContextProvider _workspaceContextProvider;
        private readonly ConcurrentDictionary<string, ProjectInfo> _projectMap;
        private bool _firstLoad, _isDisposed, _forceRefresh;
        private TestFilesUpdateWatcher _workspaceUpdateWatcher;
        private readonly HashSet<string> _pytestFrameworkConfigFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pytest.ini", "setup.cfg", "tox.ini" };

        [ImportingConstructor]
        private WSTestContainerDiscoverer(
            [Import(typeof(SVsServiceProvider))]IServiceProvider serviceProvider,
            [Import(typeof(IOperationState))]IOperationState operationState,
            [Import] IPythonWorkspaceContextProvider workspaceContextProvider
        ) {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _projectMap = new ConcurrentDictionary<string, ProjectInfo>();
            _firstLoad = true;
            _forceRefresh = false;
            _workspaceContextProvider = workspaceContextProvider ?? throw new ArgumentNullException(nameof(workspaceContextProvider));
            _workspaceContextProvider.WorkspaceClosed += OnWorkspaceClosed;
            _workspaceContextProvider.WorkspaceInitialized += OnWorkspaceLoaded;
        }

        void IDisposable.Dispose() {
            if (!_isDisposed) {
                _isDisposed = true;
                
                if (_workspaceUpdateWatcher != null) {
                    _workspaceUpdateWatcher.FileChangedEvent -= OnWorkspaceFileChanged;
                    _workspaceUpdateWatcher.Dispose();
                    _workspaceUpdateWatcher = null;
                }
                
                if (_workspaceContextProvider != null) {
                    _workspaceContextProvider.WorkspaceClosed -= OnWorkspaceClosed;
                    _workspaceContextProvider.WorkspaceInitialized -= OnWorkspaceLoaded;

                    if (_workspaceContextProvider.Workspace != null) {
                        _workspaceContextProvider.Workspace.SearchPathsSettingChanged -= OnWorkspaceSettingsChange;
                        _workspaceContextProvider.Workspace.InterpreterSettingChanged -= OnWorkspaceSettingsChange;
                        _workspaceContextProvider.Workspace.TestSettingChanged -= OnWorkspaceSettingsChange;
                    }
                }
            }
        }

        public Uri ExecutorUri {
            get {
                return PythonConstants.WorkspaceExecutorUri;
            }
        }

        public bool IsWorkspace {
            get {
                return (_workspaceContextProvider != null)
                    && (_workspaceContextProvider.Workspace != null);
            }
        }

        private bool HasLoadedWorkspace() => _workspaceContextProvider.Workspace != null;

        public IEnumerable<ITestContainer> TestContainers {
            get {
                if (HasLoadedWorkspace()
                    && (_firstLoad || _forceRefresh)) {
                    _projectMap.Clear();

                    // The first time through, we don't know about any loaded
                    // projects.
                    ThreadHelper.JoinableTaskFactory.Run(async () => {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (_firstLoad || _forceRefresh) {
                            SetupWorkspace(_workspaceContextProvider.Workspace);
                            _firstLoad = false;
                            _forceRefresh = false;
                        };
                    });
                }

                return _projectMap.Values.SelectMany(x => x.GetAllContainers());
            }
        }

        public TestContainer GetTestContainer(string projectHome, string path) {
            if (_projectMap.TryGetValue(projectHome, out ProjectInfo projectInfo)) {
                if (projectInfo.TryGetContainer(path, out TestContainer container)) {
                    return container;
                }
            }

            return null;
        }

        private void OnWorkspaceClosed(object sender, PythonWorkspaceContextEventArgs e) {
            _firstLoad = true;
            if (_workspaceUpdateWatcher != null) {
                _workspaceUpdateWatcher.FileChangedEvent -= OnWorkspaceFileChanged;
                _workspaceUpdateWatcher.Dispose();
                _workspaceUpdateWatcher = null;
            }

            if (_workspaceContextProvider.Workspace != null) {
                _workspaceContextProvider.Workspace.SearchPathsSettingChanged -= OnWorkspaceSettingsChange;
                _workspaceContextProvider.Workspace.InterpreterSettingChanged -= OnWorkspaceSettingsChange;
                _workspaceContextProvider.Workspace.TestSettingChanged -= OnWorkspaceSettingsChange;

                if (_projectMap.TryRemove(_workspaceContextProvider.Workspace.Location, out ProjectInfo projToRemove)) {
                    projToRemove.Dispose();
                }
            }

            _projectMap.Clear();
        }

        private void OnWorkspaceLoaded(object sender, PythonWorkspaceContextEventArgs e) {
            if (e.Workspace == null)
                return;

            // guard against duplicate loaded triggers
            e.Workspace.SearchPathsSettingChanged -= OnWorkspaceSettingsChange;
            e.Workspace.InterpreterSettingChanged -= OnWorkspaceSettingsChange;
            e.Workspace.TestSettingChanged -= OnWorkspaceSettingsChange;

            e.Workspace.SearchPathsSettingChanged += OnWorkspaceSettingsChange;
            e.Workspace.InterpreterSettingChanged += OnWorkspaceSettingsChange;
            e.Workspace.TestSettingChanged += OnWorkspaceSettingsChange;
        }

        private void SetupWorkspace(IPythonWorkspaceContext workspace) {
            if (workspace == null)
                return;

            bool isEnabled = workspace.GetBoolProperty(PythonConstants.PyTestEnabledSetting).GetValueOrDefault(false);

            if (isEnabled) {
                var projInfo = new ProjectInfo(this, workspace);
                _projectMap[projInfo.ProjectHome] = projInfo;

                var oldWatcher = _workspaceUpdateWatcher;
                _workspaceUpdateWatcher = new TestFilesUpdateWatcher();
                _workspaceUpdateWatcher.FileChangedEvent += OnWorkspaceFileChanged;
                _workspaceUpdateWatcher.AddDirectoryWatch(workspace.Location);
                oldWatcher?.Dispose();

                var files = Directory.EnumerateFiles(workspace.Location, "*.*", SearchOption.AllDirectories);
                foreach (var file in files) {
                    projInfo.AddTestContainer(file);
                }
            }
        }

        public ProjectInfo GetProjectInfo(string projectHome) {
            if (projectHome != null
                && _projectMap.TryGetValue(projectHome, out ProjectInfo projectInfo)) {
                return projectInfo;
            }
            return null;
        }

        public event EventHandler TestContainersUpdated;

        private void NotifyContainerChanged() {

            // guard against triggering multiple updates during initial load
            if (!_firstLoad) {
                TestContainersUpdated?.Invoke(this, EventArgs.Empty);
            }
            //try {
            //    _deferredChangeNotification.Change(100, Timeout.Infinite);
            //} catch (ObjectDisposedException) {
            //}
        }

        private void OnDeferredNotifyChanged(object state) {
            //TestContainersUpdated?.Invoke(this, EventArgs.Empty);
        }

        private IEnumerable<string> FilteredTestOrSettingsFiles(IVsProject project) {
            return project.GetProjectItems()
                .Where(s => IsTestFileOrSetting(s));
        }

        private bool IsTestFile(string path) {
            return Path.GetExtension(path).Equals(PythonConstants.FileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsTestFileOrSetting(string path) {
            return IsSettingsFile(path) || Path.GetExtension(path).Equals(PythonConstants.FileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSettingsFile(string file) {
            if (String.IsNullOrEmpty(file))
                return false;

            return _pytestFrameworkConfigFiles.Contains(Path.GetFileName(file));
        }

        private void OnWorkspaceSettingsChange(object sender, System.EventArgs e) {
            _forceRefresh = true;
            NotifyContainerChanged();
        }

        private void OnWorkspaceFileChanged(object sender, TestFileChangedEventArgs e) {
            if (String.IsNullOrEmpty(e.File))
                return;

            if (IsSettingsFile(e.File)) {
                _forceRefresh = true;
                NotifyContainerChanged();
                return;
            }

            if (!IsTestFile(e.File))
                return;

            IPythonWorkspaceContext workspace = _workspaceContextProvider.Workspace;
            if (workspace == null)
                return;

            var projInfo = GetProjectInfo(workspace.Location);
            if (projInfo == null)
                return;

            switch (e.ChangedReason) {
                case TestFileChangedReason.Added:
                    projInfo.AddTestContainer(e.File);
                    break;
                case TestFileChangedReason.Changed:
                    projInfo.AddTestContainer(e.File);
                    break;
                case TestFileChangedReason.Removed:
                    projInfo.RemoveTestContainer(e.File);
                    break;
                case TestFileChangedReason.Renamed:
                    projInfo.RemoveTestContainer(e.OldFile);
                    projInfo.AddTestContainer(e.File);
                    break;
                default:
                    break;
            }
            NotifyContainerChanged();
        }
    }
}