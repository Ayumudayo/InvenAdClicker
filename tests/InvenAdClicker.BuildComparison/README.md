# Published build comparison (Windows)

This controller launches the supplied executables and samples their complete process trees every 150ms. It does not replace the application workflow. Both builds receive copies of the old build's settings and encrypted credentials, with only DryRun and the target URL subset changed. Existing build and user data are preserved. No directory links are needed.

```powershell
dotnet publish InvenAdClicker.csproj -c Release -r win-x64 --self-contained true -o artifacts/build-comparison/publish
dotnet build tests/InvenAdClicker.BuildComparison -c Release
./tests/InvenAdClicker.BuildComparison/Run.ps1
./tests/InvenAdClicker.BuildComparison/Run.ps1 -Urls 31 -Pairs 3 -Output artifacts/build-comparison/all-urls
```

The candidate must support `--data-directory`; the old build uses its isolated working directory. When comparing two recent builds, pass `-OldUsesDataDirectory` so both receive explicit isolated paths. Each build gets one excluded warm-up, then paired runs alternate their order. Every launch uses a fresh process and browser contexts, with the existing shared browser binary and host/network caches. Output must be a fresh directory within repository `artifacts/`.

Each `result.json` contains executable identity, settings, whole-process wall time, CPU seconds, peak committed Private Bytes, samples, and per-URL collection times/counts parsed from production performance logs. An incomplete URL count or nonzero exit stops the batch. `index.json` identifies warm-ups. Raw logs and encrypted credential copies remain in ignored artifact folders; do not publish them.

Wall time has up to one sample interval of exit-detection latency. CPU includes sampled exited children through retained process handles. Very short children can escape sampling. Private Bytes measure committed private memory, not resident RAM; summed working sets double-count shared pages. The controller is excluded. Live ads rotate, so link-count differences are observations, not a ground-truth omission rate. DryRun excludes actual click-navigation performance.
