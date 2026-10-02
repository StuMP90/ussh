# ussh

A cross-platform (Windows + Linux) SSH client with tabs, saved servers, port forwarding and
sessions designed to stay up for days. Built with C#/.NET 8 and [Avalonia](https://avaloniaui.net/),
MIT licensed, and packaged as MSIX for the Microsoft Store.

## Features

- **Tabbed SSH terminals**: xterm-256color, truecolor, mouse support (vim, htop, mc), bracketed
  paste, scrollback, selection and copy/paste, CJK and emoji widths.
- **Server management**: add, edit, duplicate, delete, group and search servers. Password or
  private-key (OpenSSH/PEM, optional passphrase) authentication.
- **Admin password**: all server configuration, including passwords and keys, lives in one
  encrypted vault. The admin password is required to view or edit servers and to open
  connections. Auto-lock after inactivity; locking does **not** drop open sessions.
- **Jump hosts (bastions)**: reach a server through another saved server, which can itself
  use a jump host (chains up to 8 hops). Each hop uses that server's own credentials and
  host-key check, and the whole chain reconnects together. Loops are prevented in the editor.
- **Themes** per server, or a default in Settings, with a live preview in the picker:
  uSSH Dark, old-school **Amber (P3)**, **Green (P1)** and **White (P4)** phosphor (every
  colour rendered as a shade of the tint, like the real thing), Campbell, Solarized
  Dark/Light, Dracula, Nord, Gruvbox Dark, Monokai, One Dark, Tomorrow Night, One Light and
  High Contrast. Changing a theme updates open tabs immediately.
- **Tunnels** per server: local (`-L`), remote (`-R`) and dynamic SOCKS (`-D`), started on
  every connect. A tunnel that fails to start (port in use, etc.) is reported but never
  affects the terminal.
- **Host key verification**: trust on first use, with a loud warning if the key changes.

## Stability: how sessions stay up for days

Most "it died after 15 minutes" bugs come from a handful of causes. Each one is handled
explicitly:

| Cause | What ussh does |
|---|---|
| Idle NAT/firewall timeout | SSH keepalives (default 30s, per server) |
| Network silently gone (half-open TCP) | OS dead-peer timeout on the socket (`TCP_USER_TIMEOUT` on Linux, `TCP_MAXRT` on Windows), so a dead link fails in ~90s instead of ~15 min |
| Sleep/resume, Wi-Fi change | Resume and network-change detection probe every session immediately |
| Connection drop | Automatic reconnect with exponential backoff (1s → 60s), per tab |
| One tab's error kills the app | Each session has its own supervisor, reader thread and writer queue. Failures stay in that tab. Global handlers log anything unexpected instead of crashing |
| Memory growth over days | Fixed-size scrollback ring buffer per tab; no unbounded queues |
| UI freezes under heavy output | Output is parsed off the UI thread; the screen repaints at most ~60×/s, only when something changed |
| Terminal parser crashes or hangs on odd output | The emulator is a hardened fork (see below). A 50,000-session fuzz run passes with no failures, and every parse is guarded so a bad sequence skips a chunk instead of killing the tab |

Typing `exit` closes the tab's session cleanly, without reconnecting. Auto-reconnect starts a
**new** shell; to keep running programs across reconnects, use `tmux` or `screen` on the
server.

## Security model

- Vault: `vault.json` in `%LOCALAPPDATA%\ussh` (Windows; redirected into the package folder
  under MSIX) or `~/.config/ussh` (Linux). Override with `USSH_DATA_DIR`.
- AES-256-GCM. The key comes from the admin password via PBKDF2-SHA256 with 600,000
  iterations. The KDF parameters are authenticated, so tampering with them is detected.
- Writes are atomic, and the previous version is kept as `vault.json.bak`. On Linux the files
  are mode `0600`.
- There is no recovery: if the admin password is lost, the vault cannot be decrypted.
- While unlocked, decrypted settings are held in memory. Locking drops them. Each open
  session keeps its own copy of its server's credentials so that it can reconnect while
  locked.
- Logs (`logs/` next to the vault) never contain secrets.

## Project layout

```
src/XtermSharp/        Vendored, hardened xterm emulator engine (see NOTICE.md there)
src/Ussh.Core/         Vault, SSH sessions/reconnect/tunnels, terminal wrapper (no UI)
src/Ussh.App/          Avalonia app: views, view models, TerminalControl renderer
tests/Ussh.Core.Tests/ Unit, fuzz and end-to-end SSH tests
tests/Ussh.App.Tests/  Headless UI tests (render real windows off-screen, save screenshots)
tests/ssh-test-server/ Throwaway paramiko SSH server used by the tests
tools/Ussh.Soak/       Long-running stability/chaos tester
packaging/             MSIX (Windows/Store) and Linux tarball scripts
```

### About the vendored XtermSharp

The terminal engine comes from [XtermSharp](https://github.com/migueldeicaza/XtermSharp)
(MIT, a C# port of xterm.js). Upstream hasn't been updated since 2020, and its NuGet package
is an alpha that crashes on ordinary server output, so it is maintained here. Fuzzing found
and fixed crashes on truecolor colours, terminal resize, restored cursor positions, window
title reports and scroll regions, plus a hang where one escape sequence could freeze a tab
for minutes. The full list is in [src/XtermSharp/NOTICE.md](src/XtermSharp/NOTICE.md).

## Building and running

Requires the .NET 8 SDK.

```bash
dotnet run --project src/Ussh.App
```

## Tests

```bash
pip install paramiko          # for the end-to-end SSH tests (they pass as no-ops without it)
dotnet test
```

- `Ussh.Core.Tests`: vault crypto; terminal regressions and fuzzing; end-to-end sessions
  against a real SSH server (password/key auth, host key trust/reject/change, server
  restart → auto-reconnect, `exit`, disconnect/reconnect, resize, local/remote tunnels,
  failed tunnels, a 200k-line output flood, one- and two-hop jump hosts including a
  bastion restart and a bastion auth failure, tunnels through a jump host).
- `Ussh.App.Tests`: drives the real UI headlessly: first-run password, server management,
  lock/unlock persistence, validation, a live terminal tab, tab focus and shortcuts, a
  bastion configured in the editor, and per-server themes (checked by pixel colour). Set
  `USSH_SCREENSHOT_DIR` to keep the screenshots.

### Soak test

```bash
# Local chaos: kills and restarts the test server at random while 10 sessions run
dotnet run --project tools/Ussh.Soak -- --sessions 10 --minutes 60 --chaos-seconds 120

# Against a real server for 3 days (unplug the network / sleep the machine along the way)
dotnet run --project tools/Ussh.Soak -- --host myserver --user me --key ~/.ssh/id_ed25519 --hours 72
```

It checks every 20 seconds that each session still echoes a unique marker, and reports
reconnects, recovery times, memory, threads and handles. The exit code is non-zero if any
check failed or any session didn't recover.

## Packaging

### Microsoft Store (MSIX)

The Store signs submitted MSIX packages itself, so no code-signing certificate is needed.

1. Create a free individual account in [Partner Center](https://partner.microsoft.com/dashboard)
   and reserve the app name.
2. Copy the identity values from *Product management → Product identity*.
3. On Windows, with the Windows 10/11 SDK installed:
   ```powershell
   ./packaging/build-msix.ps1 -Version 1.0.0.0 -Architecture x64,arm64 `
       -IdentityName "<Package/Identity/Name>" -Publisher "<Package/Identity/Publisher>" `
       -PublisherDisplayName "<PublisherDisplayName>"
   ```
4. Upload `artifacts/msix/*.msix` in the submission.

For a local sideload test, use `./packaging/build-msix.ps1 -SelfSign` and follow the printed
steps to trust the dev certificate.

The GitHub Actions workflow (`.github/workflows/build.yml`) runs the tests and builds both MSIX
packages and a Linux tarball on every push. Set the repository variables `MSIX_IDENTITY_NAME`,
`MSIX_PUBLISHER` and `MSIX_PUBLISHER_DISPLAY_NAME` to have it produce Store-ready packages.

### Linux

```bash
packaging/publish-linux.sh 0.1.0 x64     # → artifacts/linux/ussh-0.1.0-linux-x64.tar.gz
```

The tarball is self-contained and includes an `install.sh` (installs to `~/.local`) and a
`.desktop` entry.

## Keyboard

| Keys | Action |
|---|---|
| Ctrl+Tab / Ctrl+PageDown | Next tab |
| Ctrl+Shift+Tab / Ctrl+PageUp | Previous tab |
| Alt+1 … Alt+8 / Alt+9 | Go to tab 1–8 / last tab |
| Ctrl+Shift+C / Ctrl+Insert | Copy selection |
| Ctrl+Shift+V / Shift+Insert / middle-click | Paste |
| Shift+PageUp / Shift+PageDown | Scroll back / forward |
| Shift+Home / Shift+End | Top / bottom of scrollback |
| Shift+drag | Select text even when an app has mouse reporting on |
| Double / triple click | Select word / line |
| Ctrl+Shift+L | Lock |
| Ctrl+S | Save server (in the editor) |

## Known limitations and roadmap

- Not yet: SFTP browser, agent forwarding, PuTTY `.ppk` keys (convert with PuTTYgen), split
  panes, custom/imported themes, and per-server font settings.
- Line reflow on resize is disabled (the upstream reflow code is unreliable); long lines are
  truncated when the window narrows, like xterm.
- Rendering redraws the whole visible screen when anything changes. That's fine for normal
  use; caching unchanged lines would cut CPU further for very large windows.
- Selection is tracked by buffer line, so it can shift once scrollback is full and still
  scrolling.
- The Windows build is compiled and packaged by CI but has not been exercised by the
  automated UI tests, which run on Linux.
