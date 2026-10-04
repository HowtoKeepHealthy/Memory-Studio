# Process control and pointer integration tests

From the repository root, run:

```powershell
.\scripts\test-process-tools.ps1
```

The runner uses the project-local .NET 10.0.401 SDK and LLVM-MinGW toolchain installed by the normal build. It builds a separate native DLL, x64/x86 fixtures, and two small managed projects that link the production services directly. Restore files, build output, fixtures, and logs stay in `artifacts/process-tools/`; the main application is not built or published.

The process control project checks 32 conditions against its own x64 and WOW64 child processes: real suspension, idempotence, preserving another suspension increment, Dispose recovery, normal controller exit recovery, and target exit cleanup. Forced controller termination cannot execute cleanup and is outside that guarantee.

The pointer project checks 24 conditions against its own x64 and WOW64 module-rooted pointer fixtures: address syntax and overflow, root-first dereferencing, bounded reverse scanning, truncation, cancellation, revalidation after target changes, and preservation of the primary memory scan history. Reverse scanning intentionally searches naturally aligned pointers with nonnegative offsets within the configured depth and budget.
