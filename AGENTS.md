# DisplayWeave agent guide

DisplayWeave has two separate implementations: the 0.5.x window/monitor-hook app (`src/DisplayWeave`, `native/legacy`) and the experimental, non-injecting virtual-display projection prototype (`src/DisplayWeave.Prototype`, `native/projection`), released separately as DisplayWeave Prototype 0.1.0-preview. **Building or installing one never installs or updates the other.**

## Read first

1. [Current handoff](docs/agent-handoff.md): user intent, verified state, unfinished work and next implementation steps.
2. [Development runbook](docs/development.md): repository layout, source map, build/test commands, installation paths and display-test recovery rules.
3. [Prototype report](docs/projection-prototype.md): measured results, command options and limitations.
4. [Architecture](docs/virtual-display-architecture.md): selected design and pinned reference research. Proposed interfaces are not implemented services.

Read the relevant source before changing it. Start with `git status --short`, `git diff --stat`, and `git log -5 --oneline`; preserve existing work. The handoff is a dated snapshot, not authority to resume work the user has since changed or cancelled.

## Product constraints

- Constrain application fullscreen to customizable regions of an ultrawide. Regions must not overlap; the existing editor uses movable ruler pins and configurable pixel spacing.
- Keep the physical desktop and taskbar at their normal dimensions. Exclude shell windows and processes, including Explorer; never implement regions by shrinking the global work area.
- The selected new approach is fullscreen on a real virtual output, projected into a physical region. It does not partition physical exclusive scanout or establish universal game compatibility.
- Keep the projection path free of application DLL injection and synthetic input. Production input gives the source real focus; tagged synthetic events belong only in explicitly owned test fixtures.
- Preserve the user's app and its original placement. Never close a game, overwrite another display manager's configuration, remove a pre-existing driver, or unload another program's hooks as routine cleanup.
- Do not bypass game protections or Windows driver-signature enforcement. Prior success with another tool is not evidence of equivalent behavior or terms-of-service approval.

## Working conventions

- Prefer the existing PowerShell scripts (`scripts/`, `tests/`, `installer/`), Windows .NET Framework (C# 5 `csc`) and pinned Zig toolchain. No new framework is needed for the current prototype work. Keep new files in the folder for their concern; do not add sources or scripts at the repository root.
- Keep legacy and projection builds, packages and settings distinct. `dist`, `release`, `.tools` and `test-results` are ignored local artifacts, not files available in a fresh clone.
- Build/policy checks are separate from live display and input tests. Live fixtures change this user's desktop; follow the runbook and current task scope. Do not run them for documentation-only changes.
- Pin window/process/device identity before mutation; a PID, HWND, display number or driver-store filename alone is insufficient. Preserve ownership checks and conditional restoration when reusing code.
- Record the tested commit, architecture, commands, results and limits. Synthetic input is not hardware input; a present-call timing is not input-to-photon latency; final restoration does not mean every field stayed unchanged during a session.
- Never report a new installer, GUI, driver broker or release as available until it exists and has been verified. The next user's desired test is with their own selected app, not another color demo.
- Update the handoff when implementation status changes. Keep `README.md` as the user-facing guide and `TESTING.md` as the evidence ledger; avoid duplicating detailed results across every document.
