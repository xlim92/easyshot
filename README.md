# EasyShot

A lightweight screenshot tool for macOS, native on Apple Silicon. Press a hotkey, select an area on any display, annotate it and copy or save the result.

## Features

- **Global hotkey**: ⌘F12 by default, can be changed from the menu bar.
- **All displays at once**: every display shows the frozen, slightly dimmed screenshot, so you can see exactly what you are selecting and start the selection on any of them.
- **Selection** with resize handles and the live size in pixels.
- **Annotation tools**: pencil, line, arrow, rectangle, filled rectangle, ellipse, highlighter, multi-line text, pixelation, numbered steps and invert.
- **Secure pixelation**: the mosaic is built only from pixels around the area, so the hidden content can't be recovered from it.
- **Editing**: move, recolor, resize and delete drawn objects, plus undo and redo.
- **Output**: copy to the clipboard or save as PNG in full Retina resolution.
- **Launch at login**: the app adds itself to Login Items on first launch.

## Requirements

- macOS 14 or later on Apple Silicon
- Xcode or the Command Line Tools with Swift 6 (`xcode-select --install`)

## Build and install

```bash
./build.sh
```

The app is built to `build/EasyShot.app`. Copy it to Applications and launch it:

```bash
ditto build/EasyShot.app /Applications/EasyShot.app
open /Applications/EasyShot.app
```

EasyShot runs in the menu bar and has no Dock icon. On first launch:

- **Screen recording**: allow it in System Settings → Privacy & Security → Screen & System Audio Recording, then restart the app.
- **Launch at login**: macOS notifies you that EasyShot was added to Login Items. You can turn it off in System Settings → General → Login Items & Extensions.

The build is signed ad hoc, so macOS may ask for the screen recording permission again after each rebuild.

## Usage

Press the hotkey or choose **Take Screenshot** from the menu bar icon, then drag to select an area. The tools appear to the right of the selection and **Copy / Save / Close** below it.

### Tools

| Tool | Key | Notes |
| --- | --- | --- |
| Select and move | V | Click an object to select it, drag to move it |
| Pencil | P | |
| Line | D | Shift snaps to 45° steps |
| Arrow | A | Shift snaps to 45° steps |
| Rectangle | S | Shift draws a square |
| Filled rectangle | R | Shift draws a square |
| Ellipse | C | Shift draws a circle |
| Highlighter | M | Shift snaps to 45° steps |
| Text | T | Enter adds a line; Esc, ⌘Enter or a click elsewhere finishes |
| Pixelate | B | Shift draws a square |
| Numbered steps | — | Click to place the next number; drag to add a pointer |
| Invert | I | Shift draws a square |

### Keyboard and mouse

| Action | How |
| --- | --- |
| Change size | Mouse wheel or trackpad scroll: line width, font size, circle radius or mosaic block size of the current tool, selected object or text being typed |
| Change the next number | Shift + scroll with the numbered steps tool |
| Pick a color | Right click, or the color button in the toolbar. With no tool active, right-clicking an object recolors it |
| Edit text | Double-click a selected text object |
| Delete the selected object | ⌫ |
| Undo / redo | ⌘Z / ⇧⌘Z |
| Copy and close | ⌘C or Enter |
| Save as PNG | ⌘S |
| Step back / close | Esc closes the color ring, then drops the tool, then deselects the object, then closes the screenshot |

The selection can be moved only while nothing is drawn, so drawings stay on what they point at. Its handles work at any time.

### Changing the hotkey

Choose **Screenshot Shortcut…** from the menu bar icon and press a new combination. It needs ⌘, ⌃ or ⌥, except F keys, which also work on their own. **Reset to ⌘F12** restores the default. The shortcut is saved between launches.

## Project structure

| File | Purpose |
| --- | --- |
| `Sources/main.swift` | App entry point |
| `Sources/AppDelegate.swift` | Menu bar item, hotkey setup, launch at login, capture flow |
| `Sources/HotKey.swift` | Global hotkey via Carbon |
| `Sources/Shortcut.swift` | Hotkey model: recording, storage, display |
| `Sources/Capture.swift` | Captures all displays with ScreenCaptureKit |
| `Sources/OverlayWindow.swift` | Full-screen overlay window for one display |
| `Sources/CaptureView.swift` | Selection, toolbars, drawing interaction, copy and save |
| `Sources/Annotation.swift` | Drawing tools and drawn objects |
| `Sources/Pixelate.swift` | Secure mosaic for hiding content |
| `make-icon.swift` | Renders the app icon during the build |
| `build.sh` | Builds and signs `build/EasyShot.app` |
