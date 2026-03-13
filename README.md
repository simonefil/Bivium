# Bivium File Manager

A web-based dual-panel file manager inspired by Norton Commander and Midnight Commander. Built with Blazor Server on .NET 10, styled with [WebTUI](https://github.com/nicholasgasior/webtui) to look like a classic terminal application.

Runs on Linux, Windows and macOS. Accessible from any browser.

![screenshot](screenshot.png)

## Features

**Dual-panel navigation** with synchronized directory trees, editable path bars with autocomplete, sortable file lists (name, size, date, attributes, owner), and full keyboard-driven operation.

**File operations** — copy, cut, paste, move, rename, delete, create files and folders. Drag and drop upload with chunked transfer (up to 50 MB per chunk). Download files or entire directories as ZIP.

**Built-in editor** powered by Monaco Editor with syntax highlighting for 40+ file types, including common languages (C#, Python, Go, Rust, TypeScript, etc.) and configuration formats (JSON, YAML, Dockerfile, etc.).

**Built-in terminal** (F12) with full shell emulation via xterm.js. Spawns the native shell of the host system.

**Archive support** — extract and create archives in ZIP, TAR, TAR.GZ, TAR.BZ2, TAR.XZ and TAR.ZST formats, with progress tracking.

**Permissions management** — view and edit file permissions. Shows Unix modes on Linux/macOS and RHSA attributes on Windows.

**Properties inspector** — file metadata, recursive directory size calculation with file/folder count.

## Keyboard shortcuts

| Key | Action |
|---|---|
| Tab | Switch active panel |
| Enter | Open directory or file |
| Backspace | Go to parent directory |
| F2 | Rename |
| F4 | Edit in Monaco editor |
| F5 | Refresh panel |
| F12 | Toggle terminal |
| Del | Delete |
| Ctrl+N | New file |
| Ctrl+Shift+N | New folder |
| Ctrl+C / X / V | Copy / Cut / Paste |
| Ctrl+A | Select all |
| Ctrl+P | Permissions |
| Alt+Enter | Properties |
| Shift+Up/Down | Extend selection |
| PageUp / PageDown | Scroll by page |
| Home / End | Jump to first / last entry |
| Shift+F10 | Context menu |

## Running with Docker

Example `docker-compose.yml` — adjust volumes, environment variables and user to match your setup:

```yaml
services:
  bivium:
    image: draknodd/bivium:latest
    container_name: bivium
    restart: unless-stopped
    user: "1000:1000"
    ports:
      - "5000:5000"
    environment:
      - BIVIUM_PORT=5000
      - BIVIUM_HOME=/data
    volumes:
      - /srv:/data:rw
```

Then open `http://your-host:5000` in your browser.

## Running standalone

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet publish Bivium/Bivium.csproj -c Release -o dist
```

Then run the compiled binary:

```bash
./dist/Bivium --port 5000
```

The port can also be set via the `BIVIUM_PORT` environment variable. `BIVIUM_HOME` controls which directory the panels open on startup — when not set, it defaults to the current user's home directory.

To build the Docker image:

```bash
docker build -t bivium .
```

## Dependencies

- [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.47.0 — archive format support
- [ZstdSharp](https://github.com/oleg-st/ZstdSharp) 0.8.7 — Zstandard compression
- [Monaco Editor](https://microsoft.github.io/monaco-editor/) — file editor
- [xterm.js](https://xtermjs.org/) — terminal emulator
- [WebTUI](https://github.com/nicholasgasior/webtui) 0.1.6 — TUI-style CSS

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
