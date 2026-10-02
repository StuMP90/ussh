# ussh

A cross-platform (Windows + Linux) SSH client with tabs, saved servers, port forwarding and
sessions designed to stay up for days. Built with C#/.NET 10 and [Avalonia](https://avaloniaui.net/),
MIT licensed, and packaged as MSIX for the Microsoft Store.

## Features

- **Tabbed SSH terminals**: xterm-256color, truecolor, mouse support (vim, htop, mc), bracketed
  paste, scrollback, selection and copy/paste, CJK and emoji widths.
- **Three kinds of server**: *SSH server* (terminal and files), *SFTP only* (files), and
  *Amazon S3 or S3-compatible storage* (files, using access keys; works with MinIO, Backblaze
  B2, Cloudflare R2 etc. via a custom endpoint). For S3 the bucket is optional: leave it empty
  to browse every bucket the keys can list (needs `s3:ListAllMyBuckets`). Buckets in different
  regions are found automatically. uSSH never creates, renames or deletes buckets: the bucket
  list is read-only, and only what's inside a bucket can be changed.
- **Dual-pane file browser** (FileZilla style), in its own tab: this computer on the left, the
  server or bucket on the right. Transfer with the arrow buttons, double-click / Enter, dragging
  between panes, or dropping files from your file manager onto the remote side. Whole folders
  copy recursively; existing files prompt *Overwrite / Overwrite if different / Keep both /
  Skip* (optionally for the rest of the transfer). *Overwrite if different* replaces a file only
  when the sizes differ or the copy being sent is newer (2-second tolerance), so re-sending a
  folder only transfers what changed. New folder, rename, delete (with a file count before confirming), and
  permissions on SFTP.
- **Transfer queue** under the panes: progress, speed, time left, cancel and retry per file,
  and a header indicator while transfers run in any tab. Dropped connections are retried
  automatically and **resume where they stopped** (SFTP offsets, S3 multipart uploads and
  ranged downloads) rather than starting again. Browsing and transfers use separate
  connections, so a big transfer never stalls browsing or any terminal.
- **Server management**: add, edit, duplicate, delete, group and search servers. Clicking a
  server shows its settings read-only; press **Edit** to change them, so connecting can't
  alter a server by accident. S3 regions are picked from a list (Europe, then the USA, then
  the rest of the world), with *Other* for anything else. Password or
  private-key authentication: OpenSSH, PEM and PuTTY `.ppk` (v2 and v3) keys, with an
  optional passphrase.
- **Admin password**: all server configuration, including passwords and keys, lives in one
  encrypted vault. The admin password is required to view or edit servers and to open
  connections. Auto-lock after inactivity; locking does **not** drop open sessions.
- **Split panes**: split a tab right or down, with the same server or any saved one, and
  drag the dividers. Each pane is its own session (own reconnect, theme and status).
  Ctrl+click several servers and Connect to open them side by side in one tab (4+ as a grid).
  **Broadcast input** sends typing to every pane in the tab, e.g. a load-balanced pair, with
  amber borders while it's on.
- **Combine and separate tabs** without reconnecting: Ctrl+click tab headers and press
  *Combine N tabs into a split*, or right-click a tab → *Combine with*. Each tab's existing
  split layout is kept. *Move to new tab* (pane menu) and *Separate panes into tabs* (tab
  menu) reverse it. Screens, scrollback and connections carry on untouched.
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

Typing `exit` ends the session cleanly, without reconnecting. By default the pane stays open
with its last output and a Reconnect button; set *When the shell exits → Close the pane* in
Settings (or per server, which overrides it) to close the pane instead, or the tab if it was the
last pane. Dropped connections and errors never auto-close. Auto-reconnect starts a **new**
shell; to keep running programs across reconnects, use `tmux` or `screen` on the server.

## Security model

- Vault: `vault.json` in `%LOCALAPPDATA%\ussh` (Windows; redirected into the package folder
  under MSIX) or `~/.config/ussh` (Linux). Override with `USSH_DATA_DIR`.
- AES-256-GCM. The key comes from the admin password via PBKDF2-SHA256 with 600,000
  iterations. The KDF parameters are authenticated, so tampering with them is detected.
- Writes are atomic, and the previous version is kept as `vault.json.bak`. On Linux the files
  are mode `0600`.
- There is no recovery: if the admin password is lost, the vault cannot be decrypted. The unlock
  screen's **Forgot the admin password?** starts again with an empty vault (after a warning and
  typing RESET). The old vault isn't deleted: it's renamed to `vault.forgotten-<date>.json` in
  the same folder, still encrypted, and can be restored by renaming it back if the password
  turns up.
- While unlocked, decrypted settings are held in memory. Locking drops them. Each open
  session keeps its own copy of its server's credentials so that it can reconnect while
  locked.
- Key passphrases can be stored in the vault, or set per server to **Ask every time (never
  stored)**. A prompted passphrase is checked locally before connecting and is kept in memory
  only: the session that used it keeps it for its own auto-reconnects, and a shared in-memory
  cache lets new panes and tabs for that server reuse it until uSSH is locked or closed.
  Nothing prompts while locked.
- Logs (`logs/` next to the vault) never contain secrets.
- S3 access key IDs and secrets are stored in the vault like passwords. Give uSSH an IAM user
  with only the access it needs on the bucket. File connections (SFTP and S3) use the same
  host-key checks, jump hosts and passphrase handling as terminals.
- uSSH always starts with no sessions open: open tabs, panes and their layout are not saved
  between runs, and won't be. Restoring them would mean reconnecting automatically at startup
  and keeping a record of what you were connected to; every run instead starts at the server
  list, behind the admin password.

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

Requires the .NET 10 SDK (`global.json` asks for 10.0.100 or a later 10.0 feature band). On
Ubuntu 24.04: `sudo apt install dotnet-sdk-10.0`; on Windows: `winget install Microsoft.DotNet.SDK.10`.

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
- SFTP and S3: browsing, folder operations, folder-tree transfers both ways (checked byte for
  byte), conflict choices, cancel/retry, permission errors, and **resume after a dropped
  connection** (the SFTP server is killed mid-transfer; S3 multipart uploads reuse their
  finished parts). S3 tests run against `moto`, a local S3 emulator, so no AWS account is
  needed: `pip install 'moto[server]'`, or point `USSH_MOTO_SERVER` at a `moto_server`. They
  pass as no-ops without it.
- `Ussh.App.Tests`: drives the real UI headlessly: first-run password, server management,
  lock/unlock persistence, validation, a live terminal tab, tab focus and shortcuts, a
  bastion configured in the editor, per-server themes (checked by pixel colour), split panes
  (shortcuts, Alt+Arrow navigation, broadcast, close), multi-select side-by-side connect, and the
  file browser for SFTP-only servers and S3 buckets. Set
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

### Releases and version numbers

Version numbers come from git tags. Push a tag like `v1.0.7`
(`git tag v1.0.7 && git push origin v1.0.7`) and the workflow (`.github/workflows/build.yml`)
runs the tests, builds that version, and creates the GitHub release "Release v1.0.7" with
generated notes and **the binaries attached**:

- `uSSH_1.0.7.0_x64.msix` and `uSSH_1.0.7.0_arm64.msix` (the Store needs four parts, ending in 0)
- `ussh-1.0.7-linux-x64.tar.gz`

The tag must be `vMAJOR.MINOR.PATCH`. Anything else (`v1.0`, `1.0.7`, `v1.0.7-beta`) fails the
build rather than producing an oddly numbered Store package. Each Store submission needs a
higher version than the last.

Pushes to `main`, pull requests and manual runs do the same tests and builds with version `0.0.<run number>`,
available only as workflow artifacts, so a test build can't be mistaken for a release. Local
builds report `0.1.0` (from `Directory.Build.props`). The running version shows in Settings → About and
on the lock screen; the log records it at startup along with the commit it was built from.

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

For Store-ready packages from CI, set the repository variables `MSIX_IDENTITY_NAME`,
`MSIX_PUBLISHER` and `MSIX_PUBLISHER_DISPLAY_NAME` (Settings → Secrets and variables →
Actions → Variables) to the values from Partner Center.

### Linux

```bash
packaging/publish-linux.sh 0.1.0 x64     # → artifacts/linux/ussh-0.1.0-linux-x64.tar.gz
```

The tarball is self-contained and includes an `install.sh` (installs to `~/.local`) and a
`.desktop` entry.

## Keyboard

The same list is in the app: **Help** in the top bar, or **Ctrl+Shift+H**.

| Keys | Action |
|---|---|
| Ctrl+Shift+H | Help (shortcuts and how things work) |
| Ctrl+Tab / Ctrl+PageDown | Next tab |
| Ctrl+Shift+Tab / Ctrl+PageUp | Previous tab |
| Alt+1 … Alt+8 / Alt+9 | Go to tab 1–8 / last tab |
| Ctrl+click tab headers | Mark tabs to combine into one split tab |
| Ctrl+Shift+E / Ctrl+Shift+O | Split pane right / down (same server; right-click or ☰ for other servers) |
| Alt+Arrow | Move to the neighbouring pane (with several panes) |
| Ctrl+Shift+W | Close pane (or the tab, if it's the last pane) |
| Ctrl+Shift+B | Broadcast input to all panes in the tab (toggle) |
| Ctrl+Shift+C / Ctrl+Insert | Copy selection |
| Ctrl+Shift+V / Shift+Insert / middle-click | Paste |
| Shift+PageUp / Shift+PageDown | Scroll back / forward |
| Shift+Home / Shift+End | Top / bottom of scrollback |
| Shift+drag | Select text even when an app has mouse reporting on |
| Double / triple click | Select word / line |
| Ctrl+Shift+L | Lock |
| Ctrl+S | Save server (in the editor) |

## Known limitations and roadmap

- File browser: S3 renames are copy-then-delete (S3 has no rename), so renaming a large folder
  is slow, and single objects over 5 GB can't be renamed in place. Files are transferred
  between this computer and the server, not directly between two servers. Dragging files out
  to the system file manager isn't supported (use download).
- Line reflow on resize is disabled (the upstream reflow code is unreliable); long lines are
  truncated when the window narrows, like xterm.
- Rendering redraws the whole visible screen when anything changes. That's fine for normal
  use; caching unchanged lines would cut CPU further for very large windows.
- Selection is tracked by buffer line, so it can shift once scrollback is full and still
  scrolling.
- The Windows build is compiled and packaged by CI but has not been exercised by the
  automated UI tests, which run on Linux.

## Licence

uSSH is released under the [MIT License](LICENSE).

The vendored terminal engine in `src/XtermSharp` keeps its own MIT licence
([src/XtermSharp/LICENSE](src/XtermSharp/LICENSE), © the xterm.js authors and Miguel de Icaza).
Other components come from NuGet under their own licences, chiefly MIT (Avalonia, SSH.NET,
CommunityToolkit.Mvvm) and Apache 2.0 (AWS SDK for .NET); all are permissive and compatible
with MIT.

Release builds (MSIX and Linux tarball) include `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`,
which lists every bundled component (including the .NET runtime and the Inter font) with its
copyright and licence text, as those licences require. It's generated per platform by
`tools/generate-third-party-notices.py` from the packages actually restored, so it stays
current when dependencies change; the packaging scripts run it, and the build fails if any
package has no licence text. In the app: **Help → Licences** or **Settings → About → Licences**.
