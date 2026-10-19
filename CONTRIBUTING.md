# Contributing

Kyklos is a one-developer tool for now, but the doors are open: issues and small pull
requests are welcome and will be read.

## Where to start

- **Report a conversion bug with the trail it leaves.** A conversion log lives at
  `%APPDATA%\Kyklos\conversions.log`, and a failing conversion writes per-file error lines
  there. Attach the relevant slice.
- **Crash reports** land in `%LOCALAPPDATA%\Programs\Kyklos\crash.log` when the app dies
  outside the self-test, and the log is the first thing to check.

## Building

```powershell
dotnet build "src\Kyklos\Kyklos.csproj"
& "src\Kyklos\bin\Debug\net9.0-windows10.0.19041.0\Kyklos.exe"
```

Run the self-test before opening a pull request — it is the project's test suite and should
report PASS on every row it can exercise (rows that need ffmpeg, LibreOffice or the HEIC
codec SKIP cleanly when a tool is not installed):

```powershell
& "Kyklos.exe" --selftest "C:\temp\selftest" all
# exit code is the number of failures; the report lands at C:\temp\selftest\selftest-report.txt
```

Continuous integration builds every push and runs that same suite on GitHub's runners, so a
pull request shows its result on the PR page.

## House rules

- Keep the honest-availability rule: formats the machine cannot produce are greyed out with
  a reason rather than offered and failed.
- Fixtures and error paths go into the self-test, not into a manual checklist.
- Commits read like sentences in the imperative mood, and explain the *why* when it is not
  the obvious *what*.

By contributing you agree your work is released under the project's MIT license.
