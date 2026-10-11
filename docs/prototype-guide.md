# DisplayWeave Prototype 0.1.0-preview

An experimental, separate app for trying the virtual-display approach with **your own app or game**. It is not the published DisplayWeave 0.5.x app and does not replace it.

During a session the prototype creates one temporary virtual display, moves the window you chose onto it, and shows that display inside a region of your physical screen. Your desktop and taskbar keep their normal size. Nothing is injected into the app and no keyboard or mouse input is synthesized. When the session ends the window goes back where it was and the temporary display is removed.

## Requirements

- Windows 10 or 11, x64, with administrator approval available (Windows asks once per session).
- Exit the legacy DisplayWeave app from its tray menu first. The prototype refuses to start while it runs.
- Internet access during installation, unless you supply the driver folder yourself.

## Install

1. Download `DisplayWeavePrototype-0.1.0-preview.zip` and its `.sha256` from the release and extract the ZIP.
2. In the extracted folder run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install-prototype.ps1
   ```

   Options: `-DesktopShortcut`, `-NoLaunch`, `-DriverPackageDirectory <folder>` (use an already downloaded copy of the pinned driver files).

The installer verifies every packaged file, downloads the pinned upstream Virtual Display Driver release asset, and checks its archive hash, file hashes and publisher signature. It installs to `%LOCALAPPDATA%\DisplayWeavePrototype\versions\0.1.0-preview` and adds a Start menu shortcut. It adds no startup entry and leaves the legacy app's settings alone. To remove it, delete `%LOCALAPPDATA%\DisplayWeavePrototype` and the Start menu shortcut.

## Use

1. Start the app you want to test in **windowed** or borderless mode on your normal display.
2. Open **DisplayWeave Prototype**, select the app, pick a region (your saved DisplayWeave regions are listed when present) and a duration (30, 60 or 90 seconds).
3. Press **Start** and approve the administrator prompt. The app moves to the virtual display and appears inside the region.
4. With keyboard and mouse control enabled, click inside the region. The app gets keyboard focus and the pointer is confined to it, so your mouse and keyboard now drive the app directly. Control starts only while the app fills the virtual display; the default "remove the window frame" option makes an ordinary window do that. Once you have control you can switch the app to its own fullscreen mode.
5. Press **Ctrl+Alt+F10** to end the session early, or press **Stop**. Otherwise it ends when the time runs out.

The virtual display is 1280 × 720 at 60 Hz. The image is scaled to fit the region and letterboxed when the shapes differ.

## Limits

- A session ends when control is released: Ctrl+Alt+F10, Alt+Tab or the Windows key end it. Pressing Start again begins a new session.
- Games that run as administrator cannot be moved by this prototype.
- Leaving exclusive fullscreen through a focus change you start yourself (Alt+Tab, Windows key) can happen before the display lease is told. The session then still cleans up, but it is reported as failed.
- Only SDR is projected. HDR, other resolutions, multiple regions at once and sessions longer than 90 seconds are not supported.
- No game compatibility, anti-cheat acceptance or terms-of-service approval is implied. You choose which app to test.

## If something goes wrong

Each session writes its reports to `%LOCALAPPDATA%\DisplayWeavePrototype\sessions\<id>`. If the window was not put back, the app shows **Restore window** the next time it opens. The temporary display removes itself when its 2-minute lease expires, even if the prototype closes.
