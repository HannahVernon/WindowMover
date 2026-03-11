# WindowMover

**Automatically move application windows to the right monitor** across multiple multi-monitor setups — home dock, work dock, laptop-only, and RDP sessions.

## Features

- **Drag-and-drop layout editor** — see your monitors as columns, drag apps to assign them
- **Automatic profile switching** — detects when monitors connect/disconnect and applies rules
- **Multiple setups supported** — home office, work office, laptop-only each get their own layout
- **RDP awareness** — separate layout profiles for remote desktop sessions
- **System tray** — runs in the background, auto-starts on login
- **Windows 10 & 11** compatible with per-monitor DPI support

## How It Works

1. **Launch WindowMover** — it detects your connected monitors and creates a "setup fingerprint"
2. **Assign apps to monitors** — drag process names from the left panel onto monitor columns
3. **Save** — your rules are stored as a JSON profile for this monitor setup
4. **Dock/undock** — WindowMover detects the change and automatically applies the matching profile

Profiles are stored in `%APPDATA%\WindowMover\profiles\`.

## Building

### Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 8.0+ | Required to compile the app |
| [Visual Studio 2022](https://visualstudio.microsoft.com/) | 17.8+ | With the **.NET desktop development** workload (or use the CLI alone) |
| [Inno Setup 6](https://jrsoftware.org/isdl.php) | 6.x | Required only for building the installer |

> **Inno Setup** must be installed to the default location (`C:\Program Files (x86)\Inno Setup 6` or `C:\Program Files\Inno Setup 6`) so the build script can find `ISCC.exe`.

### Quick build (recommended)

The `build.ps1` script handles everything — version bump, publish, and installer creation:

```powershell
.\build.ps1
```

This will:
1. Auto-increment the patch version in `Directory.Build.props`
2. Publish a self-contained x64 release to `publish\`
3. Compile the Inno Setup installer to `dist\WindowMover-Setup-<version>.exe`
4. Remove old installer versions from `dist\`

#### Build script options

| Flag | Description |
|---|---|
| `-NoBump` | Skip the automatic version increment |
| `-SkipPublish` | Skip `dotnet publish` and reuse the existing `publish\` output |
| `-RetainOldVersions` | Keep previous installer EXEs in `dist\` |
| `-Configuration Debug` | Build in Debug mode (default: Release) |

Examples:

```powershell
# Rebuild the installer without bumping the version
.\build.ps1 -NoBump

# Full build, keeping old installers
.\build.ps1 -RetainOldVersions

# Quick installer rebuild using existing publish output
.\build.ps1 -NoBump -SkipPublish
```

### Manual build steps

If you prefer to run each step individually:

```powershell
# Build only (no installer)
dotnet build

# Publish as self-contained x64
dotnet publish src\WindowMover.App -c Release -r win-x64 --self-contained --force -o publish

# Compile the installer (requires Inno Setup)
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0 installer\WindowMover.iss
```

The installer will be output to `dist\WindowMover-Setup-<version>.exe`.

## Project Structure

```
WindowMover.sln
├── build.ps1                   # Automated build, publish, and installer script
├── Directory.Build.props       # Shared version number
├── src/
│   ├── WindowMover.Core/       # Core library (models, services, Win32 interop)
│   │   ├── Models/             # MonitorInfo, MonitorSetup, WindowRule, LayoutProfile
│   │   ├── Services/           # MonitorIdentifier, MonitorWatcher, WindowManager, etc.
│   │   └── Native/             # P/Invoke declarations (User32, Wtsapi32)
│   └── WindowMover.App/        # WPF application
│       ├── ViewModels/         # MVVM ViewModels
│       ├── Controls/           # Custom WPF controls
│       └── Resources/          # App manifest, assets
├── installer/
│   └── WindowMover.iss         # Inno Setup installer script
├── publish/                    # Self-contained app output (generated)
└── dist/                       # Installer EXEs (generated)
```

## Configuration

Profiles are auto-saved as JSON files:
```
%APPDATA%\WindowMover\
├── profiles/
│   ├── A1B2C3D4E5F6G7H8.json    # Home office profile
│   ├── X9Y8Z7W6V5U4T3S2.json    # Work office profile
│   └── ...
└── .initialized                   # First-run marker
```

## Known Limitations

- **Elevated processes**: Cannot move windows of apps running as Administrator unless WindowMover is also elevated
- **UWP Store apps**: Some may resist standard window positioning APIs
- **Monitor identification**: Monitors without EDID data (rare) fall back to resolution-based identification
- **Dock switch delay**: Windows needs 2-3 seconds to stabilize after a dock/undock event; rules are applied after this debounce period

## License

MIT
