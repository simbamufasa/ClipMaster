# ClipMaster

A power-user clipboard manager for Windows that lives in your system tray, tracks everything you copy, and lets you paste any previous clip with a hotkey.

The killer feature: **regex-based transform rules** that automatically rewrite text as you copy it.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4) ![Windows](https://img.shields.io/badge/platform-Windows%2010%2B-0078D6) ![License](https://img.shields.io/badge/license-MIT-green)

## Download

**[ClipMaster-Setup-1.0.0.exe](https://github.com/simbamufasa/ClipMaster/releases/latest/download/ClipMaster-Setup-1.0.0.exe)** — single installer, no dependencies required.

## Features

### Clipboard History
- Monitors the clipboard in real time and keeps a searchable, scrollable history
- Configurable history size (default: 500 entries)
- Duplicates are automatically promoted to the top with an incremented copy count
- Pin important clips so they never get pruned
- Tag clips for quick filtering

### Regex Transform Rules
Define regex find-and-replace rules that run on every copy (or on demand):

| Mode | Behavior |
|------|----------|
| **Auto** | Applied instantly when you copy text |
| **Manual** | Available as a button on each clip card |

Rules support global (`g`), case-insensitive (`i`), and multiline (`m`) flags. A live preview in the rule editor shows the result before you save. All regex execution is capped at 1 second to prevent runaway patterns.

### Sensitive Content Masking
ClipMaster auto-detects passwords, tokens, API keys, and hex strings on the clipboard and masks them in the UI. Hover to reveal. Toggle this in Settings.

### Paste Simulation
Select a clip and ClipMaster copies it to your clipboard, hides the window, and simulates `Ctrl+V` in whatever app has focus — one-step paste from history.

### System Tray
ClipMaster lives in the tray. Left-click the icon to toggle the window; right-click for a context menu.

### Run on Startup
Optional Windows startup registration via the registry `Run` key, configurable from Settings or during install.

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+`` | Toggle ClipMaster window (global, configurable) |
| `Enter` / `Ctrl+V` | Paste the top clip |
| `Ctrl+F` | Focus the search box |
| `Esc` | Hide the window / clear search |

## UI

- Dark theme with a purple accent
- Borderless, rounded-corner window that appears near your cursor
- 4px thin scrollbars, styled cards, toast notifications
- Three tabs: **Stack** (history), **Rules** (transforms), **Settings**

## Building from Source

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10 or later

### Build & Run

```bash
dotnet run
```

### Publish a Self-Contained Executable

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Output lands in `bin/Release/net8.0-windows/win-x64/publish/`.

### Build the Installer

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php):

```bash
iscc installer.iss
```

Output: `installer/ClipMaster-Setup-1.0.0.exe`

## Data Storage

All data is stored in `%USERPROFILE%\.clipmaster\`:

| File | Contents |
|------|----------|
| `data.json` | Clips, rules, tags, and settings |
| `debug.log` | Diagnostic trace log |

## Project Structure

```
ClipMaster/
├── App.xaml.cs            Main app — tray, hotkey, clipboard wiring
├── MainWindow.xaml.cs     UI — Stack, Rules, Settings tabs
├── ClipboardMonitor.cs    Win32 clipboard listener
├── HotkeyService.cs       Win32 global hotkey registration
├── PasteService.cs        Ctrl+V simulation via SendInput
├── StartupService.cs      Registry Run key management
├── DataService.cs         JSON persistence, regex engine, password detection
├── RuleDialog.xaml.cs     Rule editor with live preview
├── TagDialog.xaml.cs      Tag management dialog
├── TrayMenu.xaml.cs       System tray context menu
├── Models.cs              Data models
├── Themes/Dark.xaml       Dark theme resources
└── Assets/                Icons and installer images
```

## Tech Stack

- **WPF** (.NET 8, C#) — UI framework
- **Win32 Interop** — clipboard monitoring (`AddClipboardFormatListener`), global hotkeys (`RegisterHotKey`), paste simulation (`SendInput`)
- **System.Text.Json** — data persistence
- **Inno Setup** — installer

## License

MIT
