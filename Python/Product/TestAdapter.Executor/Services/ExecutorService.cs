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

using Microsoft.PythonTools.Infrastructure;
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using System.IO;
using Microsoft.PythonTools.TestAdapter.Config;
using System.Diagnostics;
using System.Net;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.PythonTools.Analysis;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace Microsoft.PythonTools.TestAdapter.Services {
    public class ExecutorService : IDisposable {
        private readonly IFrameworkHandle _frameworkHandle;
        private static readonly string TestLauncherPath = PythonToolsInstallPath.GetFile("testlauncher.py");
        private static readonly Guid PythonRemoteDebugPortSupplierUnsecuredId = new Guid("{FEB76325-D127-4E02-B59D-B16D93D46CF5}");
        private readonly VisualStudioProxy _app;
        private readonly PythonProjectSettings _projectSettings;
        private readonly PythonDebugMode _debugMode;
        private readonly string _debugSecret;
        private readonly int _debugPort;
        private readonly IRunContext _runContext;

        enum PythonDebugMode {
            None,
            PythonOnly,
            PythonAndNative
        }

        /// <summary>
        /// Used to send messages to TestExplorer's Test output pane
        /// </summary>
        sealed class TestRedirector : Redirector {
            private readonly IMessageLogger _logger;

            public TestRedirector(IMessageLogger logger) {
                _logger = logger;
            }

            public override void WriteErrorLine(string line) {
                _logger.SendMessage(TestMessageLevel.Error, line);
            }

            public override void WriteLine(string line) {
                _logger.SendMessage(TestMessageLevel.Informational, line);
            }
        }

        public ExecutorService(PythonProjectSettings projectSettings, IFrameworkHandle frameworkHandle, IRunContext runContext) {
            _projectSettings = projectSettings;
            _frameworkHandle = frameworkHandle;
            _runContext = runContext;
            _app = VisualStudioProxy.FromEnvironmentVariable(PythonConstants.PythonToolsProcessIdEnvironmentVariable);
            _debugMode = (runContext.IsBeingDebugged && _app != null) ? PythonDebugMode.PythonOnly : PythonDebugMode.None;
            GetSecretAndPort(out _debugSecret, out _debugPort);
        }

        public void Dispose() {
          
        }

        public string[] GetArguments(IEnumerable<TestCase> tests, string outputfile) {
            var arguments = new List<string> {
                TestLauncherPath,
                _projectSettings.WorkingDirectory,
                "pytest",
                _debugSecret,
                _debugPort.ToString(),
                GetDebuggerSearchPath(_projectSettings.UseLegacyDebugger),
                String.Format("--junitxml={0}", outputfile)
            };

            foreach (TestCase test in tests) {
                var executionTestPath = test.GetPropertyValue<string>(Pytest.Constants.PytestTestExecutionPathPropertery, default);
                if (String.IsNullOrEmpty(executionTestPath)) {
                    Debug.WriteLine("Pytest execution path missing for testcase {0}", test.FullyQualifiedName);
                    continue;
                }
                arguments.Add(executionTestPath);
            }
            return arguments.ToArray();
        }

        private void GetSecretAndPort(out string debugSecret, out int debugPort) {
            debugSecret = "";
            debugPort = 0;

            if (_debugMode == PythonDebugMode.PythonOnly) {
                if(_projectSettings.UseLegacyDebugger) {
                    var secretBuffer = new byte[24];
                    RandomNumberGenerator.Create().GetNonZeroBytes(secretBuffer);
                    debugSecret = Convert.ToBase64String(secretBuffer)
                                        .Replace('+', '-')
                                        .Replace('/', '_')
                                        .TrimEnd('=');
                }

                SocketUtils.GetRandomPortListener(IPAddress.Loopback, out debugPort).Stop();
            }
        }

        private Dictionary<string, string> InitializeEnvironment(IEnumerable<TestCase> tests) {
            var pythonPathVar = _projectSettings.PathEnv;
            var pythonPath = GetSearchPaths(tests, _projectSettings);
            var env  = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(pythonPathVar)) {
                env[pythonPathVar] = pythonPath;
            }

            foreach (var envVar in _projectSettings.Environment) {
                env[envVar.Key] = envVar.Value;
            }

            return env;
        }

        private string GetSearchPaths(IEnumerable<TestCase> tests, PythonProjectSettings settings) {
            var paths = settings.SearchPath;

            HashSet<string> knownModulePaths = new HashSet<string>();
            foreach (var test in tests) {
                string testFilePath = PathUtils.GetAbsoluteFilePath(settings.ProjectHome, test.CodeFilePath);
                var modulePath = ModulePath.FromFullPath(testFilePath);
                if (knownModulePaths.Add(modulePath.LibraryPath)) {
                    paths.Insert(0, modulePath.LibraryPath);
                }
            }

            string searchPaths = string.Join(
                ";",
                paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            );
            return searchPaths;
        }

        public string Run(IEnumerable<TestCase> tests) {
            string ouputFile = "";
            try {
                DetachFromSillyManagedProcess();

                var env = InitializeEnvironment(tests);
                ouputFile = GetJunitXmlFile();
                var arguments = GetArguments(tests, ouputFile);

                var testRedirector = new TestRedirector(_frameworkHandle);

                using (var proc = ProcessOutput.Run(
                    _projectSettings.InterpreterPath,
                    arguments,
                    _projectSettings.WorkingDirectory,
                    env,
                    visible: true,
                    testRedirector
                )) {
                    DebugInfo("cd " + _projectSettings.WorkingDirectory);
                    DebugInfo("set " + _projectSettings.PathEnv + "=" + env[_projectSettings.PathEnv]);
                    DebugInfo(proc.Arguments);

                    if (!proc.ExitCode.HasValue) {
                        try {
                            if (_debugMode != PythonDebugMode.None) {
                                string qualifierUri = string.Format("tcp://{0}@localhost:{1}", _debugSecret, _debugPort);
                                while (!_app.AttachToProcess(proc, PythonRemoteDebugPortSupplierUnsecuredId, qualifierUri)) {
                                    if (proc.Wait(TimeSpan.FromMilliseconds(500))) {
                                        break;
                                    }
                                 }
                            }
                           
                            WaitHandle.WaitAny(new WaitHandle[] { proc.WaitHandle });
                            proc.Wait(TimeSpan.FromMilliseconds(1000));
                        } catch (COMException ex) {
                            Error(Strings.Test_ErrorConnecting);
                            DebugError(ex.ToString());
                            try {
                                proc.Kill();
                            } catch (InvalidOperationException) {
                                // Process has already exited
                            }
                        }
                    }
                }
            } catch (Exception e) {
                Error(e.ToString());
            }
            
            return ouputFile;
        }

        private string GetJunitXmlFile() {
            string baseName = "junitresults_";
            string outPath = Path.Combine(_runContext.TestRunDirectory, baseName + Guid.NewGuid().ToString() + ".xml");
            return outPath;
        }


        [Conditional("DEBUG")]
        private void DebugInfo(string message) {
            _frameworkHandle.SendMessage(TestMessageLevel.Informational, message);
        }


        [Conditional("DEBUG")]
        private void DebugError(string message) {
            _frameworkHandle.SendMessage(TestMessageLevel.Error, message);
        }

        private void Error(string message) {
            _frameworkHandle.SendMessage(TestMessageLevel.Error, message);
        }

        private static string GetDebuggerSearchPath(bool isLegacy) {
            if (isLegacy) {
                return Path.GetDirectoryName(Path.GetDirectoryName(PythonToolsInstallPath.GetFile("ptvsd\\__init__.py")));
            }

            return Path.GetDirectoryName(Path.GetDirectoryName(PythonToolsInstallPath.GetFile("Packages\\ptvsd\\__init__.py")));
        }

        private void DetachFromSillyManagedProcess() {
            var dte = _app != null ? _app.GetDTE() : null;
            if (dte != null && _debugMode != PythonDebugMode.None) {
                dte.Debugger.DetachAll();
            }
        }
    }
}