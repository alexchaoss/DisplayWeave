# Security policy

Report a vulnerability privately through
[GitHub's private vulnerability reporting](https://github.com/alexchaoss/DisplayWeave/security/advisories/new),
not in a public issue. Include the DisplayWeave version and the steps that show the problem.
Fixes go into the next release.

## How releases are protected

- Installed copies install an update only when its `.sha256` file carries a
  signature from a DisplayWeave release key. The private keys cannot be exported: one is on the
  maintainer's computer, and from 1.0.1 one is in Azure Key Vault. CI may use the Key Vault key
  only in the build of a new version on `main`, after the maintainer approves that build in
  GitHub, so access to the repository or its releases alone is not enough to ship an update.
- CI builds each release on GitHub-hosted runners and records a build-provenance attestation.
  Check a download with `gh attestation verify <file> --repo alexchaoss/DisplayWeave`.
- From 1.0.0, CI code-signs the setup, the app and its native helpers (1.0.3 and 1.0.4 also their
  PowerShell scripts; from 1.0.5 the launcher program that replaces them, and the uninstaller) as Alexandre St-Pierre (Microsoft Artifact Signing), only in the build of a
  new version on `main` after the maintainer approves it. SmartScreen may still warn while the
  publisher builds reputation. Earlier releases are unsigned; compare a download with its `.sha256`
  file from the releases page.

## Known limits

These need code already running under your Windows account. Microsoft does not treat UAC as a
security boundary between processes of the same user, and DisplayWeave does not try to add one.

- **Restart as administrator** starts `DisplayWeave.exe` and `DisplayWeave.Core.dll` from the
  install folder in `%LOCALAPPDATA%`, which your account can change. Only the native helpers are
  copied to `%ProgramFiles%\DisplayWeave` and checked against their build hashes. A program
  running as you could place a DLL in the install folder and have it run as administrator at the
  next administrator restart, behind a UAC prompt that still shows the signed publisher.
- The administrator instance reads its settings and app rules from, and writes its activity log
  to, your data folder, which your account can change or redirect.
- The names of the native hook's shared-memory and mutex objects are predictable, so another
  program running as you can create them first. This can block region control for an app. The
  hook treats their contents as data only and checks the target process again.
