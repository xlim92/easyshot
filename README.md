# EasyShot

A lightweight screenshot tool for macOS and Windows. Press a hotkey, select an area on any display, annotate it and copy or save the result.

## Features

- **Global hotkey**: ⌘F12 on macOS, Ctrl+F12 on Windows; it can be changed from the menu bar or tray icon.
- **All displays at once**: every display shows the frozen, slightly dimmed screenshot, and the selection can start on any of them.
- **Selection** with resize handles and the live size in pixels.
- **Annotation tools**: pencil, line, arrow, rectangle, filled rectangle, ellipse, highlighter, multi-line text, pixelation, numbered steps and invert.
- **Secure pixelation**: the mosaic is built only from pixels around the area, so the hidden content can't be recovered from it.
- **Editing**: move, recolor, resize and delete drawn objects, plus undo and redo.
- **Output**: copy to the clipboard or save as PNG in the full resolution of the display.
- **Launch at login**: on first launch the app adds itself to Login Items on macOS and to startup apps on Windows.

## Usage

Press the hotkey, then drag to select an area. The tools appear to the right of the selection and **Copy / Save / Close** below it.

- **Tools**: V select and move, P pencil, D line, A arrow, S rectangle, R filled rectangle, C ellipse, M highlighter, T text, B pixelate, I invert. Numbered steps are on the toolbar: click to place the next number, drag to add a pointer.
- **Shift** snaps lines to 45° steps and draws squares and circles.
- **Mouse wheel** changes the size of the current tool, the selected object or the text being typed; Shift + wheel changes the next number.
- **Right click** opens the color ring; with no tool active, it recolors the object under the cursor.
- **Double-click** a selected text object to edit it.
- **Esc** steps back: closes the color ring, drops the tool, deselects the object, then closes the screenshot.

| Action | macOS | Windows |
| --- | --- | --- |
| Copy and close | ⌘C or Enter | Ctrl+C or Enter |
| Save as PNG | ⌘S | Ctrl+S |
| Undo / redo | ⌘Z / ⇧⌘Z | Ctrl+Z / Ctrl+Shift+Z or Ctrl+Y |
| Delete the selected object | ⌫ | Backspace or Delete |
| Finish text | Esc, ⌘Enter or a click elsewhere | Esc, Ctrl+Enter or a click elsewhere |

A new hotkey needs ⌘, ⌃ or ⌥ on macOS and Ctrl or Alt on Windows; F keys also work on their own.

## macOS

Runs on macOS 14 or later on Apple Silicon. Building needs Xcode or the Command Line Tools with Swift 6.

```bash
./build.sh
```

The script builds `build/EasyShot.app` and the disk image `build/EasyShot.dmg`: open it and drag EasyShot to Applications. EasyShot lives in the menu bar. On first launch, allow screen recording in System Settings → Privacy & Security → Screen & System Audio Recording, then restart the app. The build is signed ad hoc, so macOS may ask for the permission again after each rebuild.

## Windows

Runs on 64-bit Windows 10 and 11. EasyShot lives in the notification area: a click on its icon takes a screenshot, a right click opens the menu. The exe isn't signed, so SmartScreen warns on first launch: More info → Run anyway.

There are two builds with the same features:

- `windows`: WinForms and SkiaSharp, built on a Mac with the .NET 10 SDK and Swift, which renders the icon. The script makes `build/EasyShot.exe`, a single file with the .NET runtime inside.

  ```bash
  windows/build.sh
  ```

- `windows_native`: plain Win32 and GDI+, compiled with Native AOT into a small native exe. It is built on Windows with the .NET 10 SDK and the C++ build tools of Visual Studio, whose linker Native AOT uses; the ARM64 tools are needed for ARM builds and on Windows on ARM:

  ```bat
  winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --add Microsoft.VisualStudio.Component.VC.Tools.ARM64"
  ```

  The script makes `build\native\EasyShot.exe`; `build.cmd win-arm64` builds for ARM.

  ```bat
  windows_native\build.cmd
  ```
