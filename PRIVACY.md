# zSSH privacy policy

_Last updated: 2 October 2026_

zSSH is an SSH, SFTP and S3 client. It is developed by Stuart Millington and released as open
source under the MIT licence. This policy covers the zSSH app, whether you install it from the
Microsoft Store or from a release on GitHub.

## In short

zSSH does not collect, sell or share your personal data. It has no accounts, analytics, telemetry,
advertising, crash reporting or update checks. The developer receives nothing from the app.

## Network connections

zSSH only connects to the servers and services you set up in it:

- the SSH and SFTP servers you add, including any jump hosts (bastions) you route through;
- the S3 endpoints for the storage you add: Amazon S3, or the S3-compatible service URL you enter;
- the tunnels (port forwards) you define, which carry traffic between your computer and those
  servers.

What travels over these connections is your own session: what you type, terminal output and the
files you transfer. It goes straight between your computer and your servers, encrypted by SSH or
HTTPS, and never passes through the developer.

## Data stored on your computer

zSSH keeps its data in a folder in your user profile:

- **Windows:** `%LOCALAPPDATA%\zssh`, which Windows keeps in the app's private storage when it is
  installed from the Store.
- **Linux:** `~/.config/zssh`.

That folder holds two things.

- **The vault (`vault.json`).** It contains your saved servers, usernames, passwords, saved key
  passphrases, S3 access keys, trusted host keys, tunnels and settings. It is encrypted with
  AES-256 using a key derived from your admin password. The admin password itself is never saved,
  so without it the vault can't be read, by the developer or anyone else. A backup copy of the
  previous version (`vault.json.bak`) is kept beside it.
- **Diagnostic logs (`logs/`).** Plain-text logs of connection events, such as connects,
  disconnects, reconnects, tunnel and transfer errors and app errors. They can include server
  names, host names, port numbers and file names. They never include passwords, keys, passphrases
  or what you type or see in a terminal. Logs older than 14 days are deleted automatically. They
  stay on your computer: zSSH never sends them anywhere.

zSSH reads private key files from the locations you choose, when it connects. It doesn't copy them.

While zSSH is unlocked, your decrypted settings are held in memory, and only there. Locking zSSH
discards the vault key and the decrypted settings, and forgets any key passphrases you chose not to
save. Sessions that are already open keep running while zSSH is locked, so each one keeps the
details it needs to reconnect until you close it.

If you copy or paste in a terminal, zSSH uses your system clipboard. That only happens when you
ask it to.

## Deleting your data

Uninstalling the Store version removes its data folder. To remove the data yourself, for example
after a Linux install, delete the folder listed above.

If you forget your admin password and reset the vault, the old vault is not deleted. It is renamed
to `vault.forgotten-<date>.json` and kept, still encrypted, in the same folder. Delete it there if
you no longer need it.

## Children

zSSH is a tool for managing servers. It is not aimed at children and collects no data from anyone.

## Third parties

When you install zSSH from the Microsoft Store, Microsoft handles the download, licensing and
updates under its own privacy statement: <https://privacy.microsoft.com/privacystatement>.

The servers and storage services you connect to are run by you or your providers. Their own
policies apply to the data you send them.

## Changes

If this policy changes, the new version will be published at this address with a new date. The
full history of this file is kept in the repository.

## Contact

Questions about privacy can be raised as an issue at <https://github.com/StuMP90/ussh/issues>.
