# Tunnelman

A small Windows tray app for named hops. Listen on a local port, and for each connection dial straight to a remote host or out through a SOCKS5 proxy.

Built in the same visual language as SSH Agent GUI.

## Features

- **Named tunnels** — add, edit, and delete hops
- **Enable switch** — turn a tunnel on or off from the list
- **Encrypted session** — the saved list is encrypted with a passphrase asked at startup and after a lock
- **Lock** — hide the tunnel list while hops that are already running keep accepting connections
- **Auto-lock** — lock after the keyboard and mouse have been idle, and when the window hides to the tray
- **Exit prompt** — keep the session, clear it, or cancel
- **Live log** — connect and disconnect lines for the selected tunnel
- **System tray** — minimize to the tray and keep hops running
- **Single instance** — launching again brings the existing window forward
- **Export / import** — pick tunnels and write or read a `*.tunnelman.json` file

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

The first launch asks for a passphrase. Settings and the encrypted tunnel session live under `%AppData%\Tunnelman\`.

`*.tunnelman.json` exports are encrypted with the session passphrase. The file on disk is an envelope. The passphrase protects each tunnel's `name`, `listenAddress`, `listenPort`, `socksHost`, `socksPort`, `remoteHost`, and `remotePort`. SOCKS can be left empty. Exports do not include enable state or `ui.json`. Imported tunnels stay off until you enable them. A Release build stamps `0.1.0.N` from `BuildNumber.txt`.

## Usage

| Action | How |
| --- | --- |
| Add a tunnel | **Add tunnel**, or `Ctrl+N` |
| Export tunnels | **Export**, or `Ctrl+E` |
| Import tunnels | **Import**, or `Ctrl+I` |
| Start or stop | Flip the switch on the row |
| Edit / delete | Row buttons on the right |
| Lock | **Lock**, or the auto-lock control beside it |
| Hide the window | `Escape` (app stays in the tray; locks when auto-lock is on) |
| Exit | Close the window or tray **Exit**, then keep or clear the session |

## Development

- .NET 9
- WPF UI
- Tests: `dotnet test Tunnelman.sln`
