﻿using Microsoft.PythonTools.Infrastructure;
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
        private readonly PythonDebugMode _debugMode;
        private readonly string _debugSecret;
        private readonly int _debugPort;

        enum PythonDebugMode {
            None,
            PythonOnly,
            PythonAndNative
        }

        public ExecutorService(IFrameworkHandle frameworkHandle,IRunContext runContext) {
            _frameworkHandle = frameworkHandle;
            _app = VisualStudioProxy.FromEnvironmentVariable(PythonConstants.PythonToolsProcessIdEnvironmentVariable);
            _debugMode = PythonDebugMode.None;
            if (runContext.IsBeingDebugged && _app != null) {
                _debugMode = PythonDebugMode.PythonOnly;
            }

            _debugSecret = GetSecretAndPort(out _debugPort);
        }

        public void Dispose() {
          
        }

        public string[] GetArguments(IEnumerable<TestCase> tests, PythonProjectSettings projSettings, string outputfile) {
            var arguments = new List<string>();
            arguments.Add(TestLauncherPath);
            arguments.Add(projSettings.WorkingDirectory);
            arguments.Add(projSettings.PytestPath);
            arguments.Add(_debugSecret);
            arguments.Add(_debugPort.ToString());
            arguments.Add(String.Format("--junitxml={0}", outputfile));

            if (!String.IsNullOrEmpty(projSettings.PytestArgs))
                arguments.Add(projSettings.PytestArgs);

            foreach (var test in tests) {
                var pytestId = test.GetPropertyValue<string>(Pytest.Constants.PytestIdProperty, default(string));
                var executionTestPath = Path.Combine(Path.GetDirectoryName(test.CodeFilePath), pytestId);
                if (String.IsNullOrEmpty(executionTestPath)) {
                    Debug.WriteLine("PytestId missing for testcase {0}", test.FullyQualifiedName);
                    continue;
                }
                arguments.Add(executionTestPath);
            }
            return arguments.ToArray();
        }

        private string GetSecretAndPort(out int debugPort) {
            string debugSecret = "";
            debugPort = 0;
            if (_debugMode == PythonDebugMode.PythonOnly) {
                var secretBuffer = new byte[24];
                RandomNumberGenerator.Create().GetNonZeroBytes(secretBuffer);
                debugSecret = Convert.ToBase64String(secretBuffer)
                                    .Replace('+', '-')
                                    .Replace('/', '_')
                                    .TrimEnd('=');
                SocketUtils.GetRandomPortListener(IPAddress.Loopback, out debugPort).Stop();
            }
            return debugSecret;
        }

        private Dictionary<string, string> InitializeEnvironment(IEnumerable<TestCase> tests, PythonProjectSettings projSettings) {
            var pythonPathVar = projSettings.PathEnv;
            var pythonPath = GetSearchPaths(tests, projSettings);
            var env  = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(pythonPathVar)) {
                env[pythonPathVar] = pythonPath;
            }

            foreach (var envVar in projSettings.Environment) {
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

            paths.Insert(0, settings.WorkingDirectory);

            if (_debugMode == PythonDebugMode.PythonOnly) {
                paths.Insert(0, PtvsdSearchPath);
            }

            string searchPaths = string.Join(
                ";",
                paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            );
            return searchPaths;
        }

        public string Run(PythonProjectSettings projSettings, IEnumerable<TestCase> tests) {
            string ouputFile = "";
            try {
                DetachFromSillyManagedProcess();

                var env = InitializeEnvironment(tests, projSettings);
                ouputFile = GetJunitXmlFile();
                var arguments = GetArguments(tests, projSettings, ouputFile);

                using (var proc = ProcessOutput.Run(
                    projSettings.InterpreterPath,
                    arguments,
                    projSettings.WorkingDirectory,
                    env,
                    visible: true,
                    null
                )) {

                    DebugInfo("cd " + projSettings.WorkingDirectory);
                    DebugInfo("set " + projSettings.PathEnv + "=" + env[projSettings.PathEnv]);
                    DebugInfo(proc.Arguments);

                    if (!proc.ExitCode.HasValue) {
                        try {
                            if (_debugMode != PythonDebugMode.None) {
                                string qualifierUri = string.Format("tcp://{0}@localhost:{1}?legacyUnitTest", _debugSecret, _debugPort);
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
            var tempFolder = Path.Combine(Path.GetTempPath(), "pytest");
            Directory.CreateDirectory(tempFolder);

            string baseName = "junitresults_";
            string outPath = Path.Combine(tempFolder, baseName + Guid.NewGuid().ToString() + ".xml");
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

        private static string PtvsdSearchPath {
            get {
                return Path.GetDirectoryName(Path.GetDirectoryName(PythonToolsInstallPath.GetFile("ptvsd\\__init__.py")));
            }
        }

        private void DetachFromSillyManagedProcess() {
            var dte = _app != null ? _app.GetDTE() : null;
            if (dte != null && _debugMode != PythonDebugMode.None) {
                dte.Debugger.DetachAll();
            }
        }
    }
}
