# Tunnelman

A small Windows tray app for named hops. Listen on a local port, and for each connection dial straight to a remote host or out through a SOCKS5 proxy.

Built in the same visual language as SSH Agent GUI.

## Features

- **Named tunnels** — add, edit, and delete hops
- **Enable switch** — turn a tunnel on or off from the list
- **Plain session** — the saved list is a JSON file and the app opens onto it
- **Exit prompt** — keep the session, clear it, or cancel
- **Live log** — connect and disconnect lines, opened from the row
- **Reorder** — drag a row by its grip, or move the selection with Alt+Up and Alt+Down
- **System tray** — minimize to the tray and keep hops running
- **Single instance** — launching again brings the existing window forward
- **Export / import** — pick tunnels and write or read a plain `*.tunnelman.json` file

## Requirements

- Windows 10 or 11
- [.NET 9 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)

## Getting started

```bat
dotnet test Tunnelman.sln
dotnet build Tunnelman.sln -c Release
```

Run:

```bat
src\Tunnelman\bin\Release\net9.0-windows\Tunnelman.exe
```

The tunnel list lives in `%AppData%\Tunnelman\tunnels.json`. Window placement lives in `ui.json` beside it. Enabled tunnels start when the app opens.

`*.tunnelman.json` exports are plain JSON. Each tunnel includes `name`, `enabled`, `listenAddress`, `listenPort`, `socksHost`, `socksPort`, `remoteHost`, and `remotePort`. SOCKS can be left empty. Import keeps the enabled flag from the file. A Release build stamps `0.1.0.N` from `BuildNumber.txt`.

## Usage

| Action | How |
| --- | --- |
| Add a tunnel | **Add tunnel**, or `Ctrl+N` |
| Export tunnels | **Export**, or `Ctrl+E` |
| Import tunnels | **Import**, or `Ctrl+I` |
| Start or stop | Flip the switch on the row |
| Log | **Log** on the row. Escape closes that window |
| Reorder | Drag the grip, or Alt+Up / Alt+Down |
| Edit / delete | Row buttons on the right |
| Hide the window | `Escape` (the app stays in the tray) |
| Exit | Close the window or tray **Exit**, then keep or clear the session |

## Development

- .NET 9
- WPF UI
- Tests: `dotnet test Tunnelman.sln`
