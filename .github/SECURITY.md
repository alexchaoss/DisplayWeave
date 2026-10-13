# Security policy

Report a vulnerability privately through
[GitHub's private vulnerability reporting](https://github.com/alexchaoss/DisplayWeave/security/advisories/new),
not in a public issue. Include the DisplayWeave version and the steps that show the problem.
Fixes go into the next release.

## How releases are protected

- Installed copies and `install.ps1` install a release only when its `.sha256` file carries a
  signature from a DisplayWeave release key. The private key is kept on the maintainer's
  computer and never on GitHub, so access to the repository or its releases is not enough to
  ship an update.
- CI builds each release on GitHub-hosted runners and records a build-provenance attestation.
  Check a download with `gh attestation verify <file> --repo alexchaoss/DisplayWeave`.
- From 1.0.0, CI code-signs the setup, the app and its native helpers as Alexandre St-Pierre
  (Microsoft Artifact Signing), only in the build of a new version on `main` after the maintainer
  approves it. SmartScreen may still warn while the publisher builds reputation. Earlier releases
  are unsigned; compare a download with its `.sha256` file from the releases page.
