
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.PythonTools.Profiling {
    /// <summary>
    /// Launch profiling via Visual Studio Diagnostic Tools on VS2022+.
    /// </summary>
    internal static class DiagnosticProfilerLauncher {
        /// <summary>
        /// Attempts to start profiling for the provided executable using VS Diagnostic Tools asynchronously.
        /// Returns true if the modern API is available and launch was initiated; otherwise false.
        /// </summary>
        public static async Task<bool> TryStartProfilingAsync(string exePath, string arguments, string workingDirectory, string outputFile) {
            try {
                // Ensure we're on the main thread for VS service calls
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Get the IVsProfileLauncher service
                var launcher = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(VsProfileLauncher)) as IVsProfileLauncher;
                if (launcher == null) {
                    return false; // Service not available
                }

                // Launch a profiling session using a built-in profile (e.g., "CPU Usage")
                // NOTE: You may need to define or query available profiles for Python scenarios.
                int hr = launcher.LaunchProfile("CPU Usage");
                return hr == 0; // S_OK
            } catch {
                return false;
            }
        }
    }
}

