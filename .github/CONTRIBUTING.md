# Contributing to DisplayWeave

Thanks for helping. Bug reports, small fixes and documentation improvements are all welcome.
Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Bugs:** open an issue with the bug report form. **Status & help → Diagnostic report…**
  builds the details for you.
- **Security problems:** report them privately, as [SECURITY.md](SECURITY.md) explains.
- **Larger changes or new features:** open an issue first so we can agree on the approach
  before you spend time on it.

## What fits the project

These are firm limits; a change that breaks one will not be merged.

- Regions are a fixed grid that never overlaps. Do not implement them by shrinking the
  global work area; the desktop and taskbar keep their normal size.
- Never hook shell windows or processes, including Explorer.
- Do not bypass game protections or Windows driver-signature enforcement.
- Preserve the user's apps and their original placement. Never close an app, overwrite
  another display manager's configuration, or unload another program's hooks as cleanup.
- Pin a window's or process's identity before changing it; a PID or HWND alone is not enough.

## Building and testing

You need Windows 10/11 x64. The build uses the .NET Framework C# 5 compiler that ships
with Windows and a pinned Zig toolchain; there is no extra framework to install.

```powershell
.\scripts\fetch-tools.ps1
.\scripts\build-native.ps1 -Architecture all
.\scripts\build.ps1
.\dist\DisplayWeave.exe --self-test managed-test-results.txt
```

The [development runbook](../docs/development.md) has the repository layout and the full
list of build and test commands. The tests under `tests/` that drive real windows change
your desktop, so read the runbook before running them. Documentation-only changes need no
tests. CI builds both architectures and runs the non-interactive checks on every pull request.

## Code and files

- Match the surrounding code: naming, comment density and idiom. Code must compile as C# 5.
- Put new files in the folder for their concern (`scripts/`, `tests/`, `installer/`,
  `docs/`); do not add sources or scripts at the repository root.
- `dist`, `release`, `.tools` and `test-results` are local artifacts and are not committed.
- `README.md` is the user guide and [docs/testing.md](../docs/testing.md) is the evidence
  ledger. Update the one that matches your change rather than repeating details in both.

## Pull requests

1. Fork the repository and branch from `main`.
2. Keep each pull request to one change.
3. Fill in the pull request template, including what you tested and on which Windows version.
4. Release builds, signing and version bumps are done by the maintainer; do not change
   `version.txt` or `release.json`.

By contributing you agree that your work is released under the [MIT License](../LICENSE).
