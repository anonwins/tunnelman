# Tunnelman

A small Windows app for named ncat SOCKS5 hops. Listen on a local port, and for each connection run Nmap ncat through a local SOCKS5 proxy to a remote host and port.

Built as a tray app in the same visual language as SSH Agent GUI.

## Features

- **Named tunnels** — add, edit, and delete hops
- **Enable switch** — turn a tunnel on or off from the list
- **Session memory** — entries save immediately; the next launch starts every tunnel that is still enabled
- **Exit prompt** — keep the session, clear it, or cancel
- **Live ncat log** — verbose output for the selected tunnel
- **System tray** — minimize to the tray and keep hops running
- **Single instance** — launching again brings the existing window forward
- **Export / import** — pick tunnels and write or read a `*.tunnelman.json` file

## Requirements

- Windows 10 or 11
- [.NET 9 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Nmap ncat](https://nmap.org/download.html) (`ncat.exe` with `--proxy` and `--exec`)

Tunnelman does not bundle ncat. Nmap’s license (NPSL) applies to ncat itself.

## Getting started

```bat
dotnet test Tunnelman.sln
dotnet build Tunnelman.sln -c Release
```

Run:

```bat
src\Tunnelman\bin\Release\net9.0-windows\Tunnelman.exe
```

If ncat is missing, browse to `ncat.exe` or install official Nmap (winget `Insecure.Nmap`, or the installer from nmap.org). Common location: `C:\Program Files (x86)\Nmap\ncat.exe`.

Settings and the tunnel session live under `%AppData%\Tunnelman\`.

`*.tunnelman.json` exports are copy-only: they do not include enable state or `ui.json`. Imported tunnels stay off until you enable them.

## Usage

| Action | How |
| --- | --- |
| Add a tunnel | **Add tunnel**, or `Ctrl+N` |
| Export tunnels | **Export**, or `Ctrl+E` |
| Import tunnels | **Import**, or `Ctrl+I` |
| Start or stop | Flip the switch on the row |
| Edit / delete | Row buttons on the right |
| Hide the window | `Escape` (app stays in the tray) |
| Exit | Close the window or tray **Exit**, then keep or clear the session |

## Development

- .NET 9
- WPF UI
- Tests: `dotnet test Tunnelman.sln`
