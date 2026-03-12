# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RemoteX** is a Windows desktop remote connection manager built with WPF (.NET 8). It supports RDP, SSH, and Telnet connections in a multi-tab interface, with optional SOCKS5 proxy routing. There is also an optional SOCKS5 jump server component written in Go (`SocksServer/`).

## Build Commands

**Debug (run locally):**
```bash
dotnet run
```

**Release (single-file executable):**
```bash
dotnet publish -c Release -p:PublishProfile=SingleFile
# Output: bin\Release\net8.0-windows\win-x64\publish\RemoteX.exe
```

**SOCKS5 server component (optional, Go):**
```bash
cd SocksServer && build.bat
# Output: proxy.exe
```

There are no automated tests in this project.

## Architecture

### MainWindow Partial Classes

`MainWindow.xaml.cs` is split across multiple partial class files by concern:

| File | Responsibility |
|---|---|
| `MainWindow.Connect.cs` | Opening RDP/SSH/Telnet connections |
| `MainWindow.Crud.cs` | Add/edit/delete server entries |
| `MainWindow.Health.cs` | Batch TCP health checks |
| `MainWindow.ImportExport.cs` | JSON export/import |
| `MainWindow.Sidebar.cs` | Server list filtering and selection |
| `MainWindow.Tabs.cs` | Multi-session tab lifecycle |
| `MainWindow.Terminal.cs` | Terminal I/O bridge (WebView2 ↔ SSH.NET) |

### Session Layer

- **`RdpSessionService.cs`** — Wraps the `AxMsRdpClient9NotSafeForScripting` COM control. RDP uses a WinForms `Panel` host embedded in WPF via `WindowsFormsHost`.
- **`TerminalSessionService.cs`** — Manages SSH (SSH.NET) and Telnet (raw `TcpClient`) sessions. Bridges data to/from the xterm.js frontend via WebView2 `PostWebMessageAsString`.
- **`SocksProxyBridge.cs`** — Listens on a random local port and tunnels traffic through SOCKS5 to the target. Used to give `AxMsRdpClient` a local address when an RDP connection must go through a proxy.

### Terminal UI

The terminal frontend (`Assets/terminal.html` + xterm.js) runs inside a WebView2 control. The C# side sends terminal data as base64 strings via `PostWebMessageAsString`; the JS side posts keystrokes back. `AssetExtractor.cs` unpacks these embedded assets to `%LocalAppData%\RemoteX\assets\` on first run.

### SOCKS5 Stack

- **`Socks5Helper.cs`** — Low-level SOCKS5 handshake (RFC 1928) with optional TLS upgrade and SHA-256 certificate pinning.
- **`SocksProxyBridge.cs`** — Local loopback port-forward through SOCKS5 (used for RDP proxy routing).
- **`SocksServer/main.go`** — Optional Go binary to deploy as a SOCKS5 jump server; supports TLS auto-sniffing on a single port and Windows service installation.

### Data & Configuration

- **`ServerRepository.cs`** — Async SQLite CRUD via `Microsoft.Data.Sqlite`. Schema migrations run automatically on startup.
- **`AppSettings.cs`** — JSON settings at `%LocalAppData%\RemoteX\appsettings.json`. Includes SOCKS proxy list and recent server list. Automatically migrates from the legacy `MyRdpManager` folder name.
- **`CredentialProtector.cs`** — DPAPI (`ProtectedData`) for at-rest password encryption in the database and settings file.
- **`ServerExportImport.cs`** — AES-256-GCM encrypted JSON export/import with PBKDF2 key derivation.

### Key Enums (in `ServerInfo.cs`)

```csharp
enum ServerProtocol  { RDP, SSH, Telnet }
enum ServerHealthState { Unknown, Checking, Online, Offline }
```

## Data Storage Paths

All runtime data lives under `%LocalAppData%\RemoteX\`:
- `servers.db` — SQLite database
- `appsettings.json` — Application settings
- `ui-state.json` — Window/UI state
- `assets/` — Extracted xterm.js files
- `logs/` — Serilog log files

## Key Dependencies

| Package | Purpose |
|---|---|
| `SSH.NET` | SSH client |
| `Microsoft.Web.WebView2` | Terminal UI host |
| `Microsoft.Data.Sqlite` | Database |
| `Serilog` + `Serilog.Sinks.File` | Logging |
| `AxMSTSCLib.dll` / `MSTSCLib.dll` | RDP ActiveX (local DLLs) |

## WPF + WinForms Interop Note

The project enables both `UseWPF` and `UseWindowsForms` because the RDP ActiveX control (`AxMsRdpClient`) requires a WinForms container. This means `WindowsFormsHost` is used in certain tab content areas.
