# Migrate PTVS Profiling to VS2022 Diagnostic Tools

## Summary
Legacy `VSPerfMon.exe` was removed in VS2022+. PTVS profiling must use Diagnostic Tools APIs instead of launching perfmon/cmd binaries.

## Goals
- Remove dependency on `VSPerfMon.exe` and `VSPerfCmd.exe`
- Start/stop profiling via VS Diagnostic Tools (e.g., `IVsProfileLauncher` / Diagnostics Hub APIs)
- Keep existing Performance Explorer UX and session model

## Steps
1. Locate legacy profiler invocations
- `Product/Profiling/Profiling/ProfiledProcess.cs` (`StartPerfMon`, `StopPerfMon`, `GetPerfToolsPath`)
- Tests using `vsperfreport.exe` in `Tests/ProfilingUITests/ProfilingUITests.cs`
2. Assess profiling entry points
- Session start in `PythonProfilingPackage.RunProfiler` and `SessionNode.StartProfiling`
- Python native bridge `Product/VsPyProf` loads `VsPerf*.dll`
3. Introduce profiler service usage
- Acquire `IVsProfileLauncher` from `SVsProfileLauncher`: `await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsProfileLauncher))`
- Alternatively use Diagnostics Hub APIs (`IDiagnosticSession`) for CPU sampling
4. Implement profiling session start
- Replace `StartPerfMon/StopPerfMon` calls with `IVsProfileLauncher.LaunchProfileAsync(...)` (or equivalent API) passing target exe/args/working dir
- Manage session lifetime to stop on process exit
5. Update UI integration
- Maintain `PerfToolWindow` and `SessionNode` workflow
- Still produce report artifacts (switch to `.diagsession` or exported report format). Adjust opening logic
6. Add version gating
- VS2019 and earlier: keep legacy path if available
- VS2022+: use new API only
7. Remove hardcoded paths
- Delete `GetPerfToolsPath`, `VSPerfMon.exe`/`VSPerfCmd.exe` lookups
8. Add fallbacks for Python-only profiling
- Consider `cProfile`/`py-spy` integration when Diagnostic Tools is unavailable
9. Update tests
- Replace `vsperfreport.exe` CSV conversion with Diagnostics Hub report queries or skip conversion
- Gate legacy tests under `#if !DEV17_OR_LATER`
10. Update docs
- Document migration, supported VS versions, and limitations

## Files
- Product/Profiling/Profiling/ProfiledProcess.cs (modify) - replace legacy perfmon logic
- Product/Profiling/PythonProfilingPackage.cs (modify) - acquire profiler services
- Product/VsPyProf/* (modify) - gate or remove VsPerf dll loading for VS2022+
- Tests/ProfilingUITests/ProfilingUITests.cs (modify) - remove vsperfreport dependency

## Notes
- Ensure async service retrieval on UI thread where required
- Preserve session report UX; consider `.diagsession` handling
- Keep changes minimal, guarded by VS version symbols

---

## Combined Migration Checklist (Profiling + Extension Architecture)

### 1. Remove Legacy Profiler Dependencies
- [ ] Delete any hardcoded references to `VSPerfMon.exe` or related binaries.
- [ ] Remove old COM-based profiler integration code.
- [ ] Audit for any synchronous process spawning for profiling.

### 2. Adopt Modern Profiling APIs
- [ ] Use `Microsoft.VisualStudio.Profiler.dll` and `IVsProfileLauncher` or `IDiagnosticSession`.
- [ ] Acquire services asynchronously:

```csharp
var launcher = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsProfileLauncher)) as IVsProfileLauncher;
```

- [ ] Launch profiling sessions via `LaunchProfileAsync` instead of external processes.
- [ ] Integrate Python profiling using `cProfile` or `py-spy` and feed results into Visual Studio diagnostics.
- [ ] Surface results in the Performance Profiler tool window.

### 3. Migrate to `AsyncPackage`
- [ ] Replace `Package` with `AsyncPackage` in your extension.
- [ ] Implement `InitializeAsync` (proffer services, register tool windows only).
- [ ] Use `JoinableTaskFactory` for main-thread switches:

```csharp
await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
```

### 4. Proffer Services Using Lambdas
- [ ] Register services with async lambdas:

```csharp
this.AddService(typeof(IMyService), async (container, type, cancellationToken) =>
{
    await Task.Yield();
    return new MyService();
});
```

### 5. Eliminate Synchronous Patterns
- [ ] Replace `GetService` with `GetServiceAsync`.
- [ ] Remove blocking calls (`.Result`, `.Wait()`).
- [ ] Ensure all I/O and profiler calls are async.

### 6. Improve Testability
- [ ] Remove mutable statics.
- [ ] Use dependency injection for profiler integration.
- [ ] Keep logic out of `AsyncPackage`—move to services for easier unit testing.

### 7. Validate Threading Rules
- [ ] Ensure UI updates happen on the main thread via `JoinableTaskFactory`.
- [ ] Keep background work off the UI thread.

### 8. Modern Setup Authoring
- [ ] Update VSIX manifest for VS2022 compatibility.
- [ ] Remove deprecated registry-based configuration.
- [ ] Use the latest VS SDK NuGet packages.

### Next Steps
- Draft a sample skeleton extension showing `AsyncPackage` + `IVsProfileLauncher` integration for Python profiling.
- Create a detailed migration map (old code ? new code) for PTVS profiling logic.