# Forge for Windows

A tray app for watching and managing Laravel Forge servers on Windows. It's a port of
[omarchy-forge](https://github.com/acobrerosf/omarchy-forge), the Omarchy bar widget, to C# / WinUI 3.

- **Tray icon** with one badge for the worst state across every watched organization:
  none, hollow (maintenance), pulsing (deploy running), amber (token or request problem),
  red (server unreachable or deploy failed). The tooltip summarises it.
- **Flyout** (left-click the tray icon): servers and their sites, with search, organization tabs and
  chips for anything failed, deploying or in maintenance. Hover a row for Deploy and Open site, or
  right-click it for everything else. Works fully from the keyboard too.
  - Site: deploy, maintenance mode, PHP-FPM reload, run a command, heartbeats, open site, open in Forge,
    copy ssh, open ssh in Windows Terminal, and three sections: **Deployments** (every deployment and its log),
    **Logs** (application, nginx error, nginx access; clear them too) and **Scheduled jobs** (with the last run's output).
  - Server: restart nginx, PHP-FPM, supervisor, redis and the database (only the services the server type
    runs), reboot, recipes, monitors, and four sections: **Events**, **Logs** (nginx, PHP, database, redis,
    ssh… only the ones the server can have; clear them too), **Background processes** (status and log;
    restart one) and **Scheduled jobs**.
- **Window** (the flyout's ⧉ button, Ctrl+O, or the tray menu): the same servers and sites with room to read.
  Each section is one tab laid out the same way: the list on the left, and beside it the output of the
  chosen item, with find, wrap, copy and save. A tab opens on its first item (the newest deployment or
  event), so there is always something to read.
- **Nothing extra on the poll.** History, logs, processes and jobs are read only when you open them.
- **Toasts** when a deployment finishes or fails, with Open site, View log and Open in Forge buttons.
- **Every write asks first** in a small dialog. Cancel has the focus, so Enter and Esc both drop it;
  only the dialog's own button, or a capital **Y**, sends it.

## Keys (flyout)

| Key | Does |
|---|---|
| `↑` `↓` / `j` `k` | move |
| `Enter` | fold a server, open a site, choose an action |
| `→` / `l` | drill in (a server's actions) |
| `←` / `h` / `Backspace` | back |
| `Esc` | back, then close |
| `Ctrl+F` | search |
| `Ctrl+O` | open in a window |
| `d` | deploy the current site (asks first) |
| `m` | maintenance mode, on a site's page (asks first) |
| `Y` | confirm what a dialog asks |
| `o` / `f` / `s` / `t` | open site / open in Forge / copy ssh / ssh in Windows Terminal |
| `e` | the server's events |
| `r` | refresh (or reload the open log) |
| `c` / `w` | in a log: copy / save to Downloads |
| `x` | in a site or server log: clear it; in a process's log: restart the process (asks first) |
| `,` | settings |
| `?` | these keys |

## Install

Download the latest **setup.exe** from [Releases](https://github.com/acobrerosf/forge-for-windows/releases)
(`x64` for most PCs, `arm64` for Windows on ARM) and run it. It installs for you only, with no admin
rights, and adds Forge for Windows to the Start menu and to Settings › Apps for uninstalling. To
update, run a newer setup; your accounts and tokens are kept. The installer isn't code-signed, so
Windows may say it's from an unknown publisher: choose **More info › Run anyway**. Each release also
has a portable zip that runs without installing.

### From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. It isn't needed to run the installed app.

```powershell
git clone https://github.com/acobrerosf/forge-for-windows.git
cd forge-for-windows
powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Build
```

The script installs for the current user only, with no admin rights:

- it copies the app to `%LOCALAPPDATA%\Programs\ForgeForWindows`;
- it adds a **Forge for Windows** Start menu shortcut;
- it starts the app.

To update, run `git pull` and then run the same install command again; your accounts and tokens are kept.
To remove the app, run `tools\uninstall.ps1`. Add `-Purge` to also delete your settings and stored tokens.
The script and the setup install to the same place, so either one updates an install made by the other.

### Making a release

Push a version tag and GitHub Actions builds the x64 and ARM64 installers and portable zips
(`.github/workflows/release.yml`, using `tools/installer.iss`) and attaches them to a draft release:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

Then read the draft on the Releases page and press **Publish release**. Running the workflow by hand
from the Actions tab builds the same files as a downloadable artifact, without making a release.

## Setup

1. Create a token at <https://forge.laravel.com/profile/api>. Read access to servers and sites is enough
   to watch them, and to list deployments, background processes and scheduled jobs and read a job's output.
   Each write scope is opt-in: `site:manage-deploys` (deploy, read deployment logs), `server:manage-services`,
   `site:manage-commands`, `server:manage-logs` (read and clear site and server logs),
   `server:create-daemons` (read a background process's log and restart it), `recipe:view` and `recipe:manage`.
   A view whose scope is missing says which one it needs.
2. Start the app. On first run Settings opens. Paste the token, press **Check token**, pick the
   organizations to watch, then press **Save account**.

What is stored where:

- **Tokens:** Windows Credential Manager, under Generic credentials named `ForgeForWindows:<account>`.
  They are never written to a file.
- **Everything else** (accounts, organizations, settings): `%LOCALAPPDATA%\ForgeForWindows\state.json`.
- **"Start with Windows":** `HKCU\...\Run\ForgeForWindows`. The app then starts with `--background`.

## Rate limit

Forge allows 60 requests a minute per Forge user, shared with your browser. The app keeps itself to 40.

- Each refresh costs **2 requests per organization**: the server list, then one window of the organization's site list.
- Past 150 sites, the site list is checked in rotation.
- While a deploy runs, the app looks every 15 seconds instead of at the normal interval.
- A 429 response pauses every organization behind that Forge user until Forge's reset time.

## Build

You need the .NET 10 SDK. Visual Studio is optional, since everything builds with the `dotnet` CLI.

```bash
dotnet test --project Forge.Core.Tests
```

```bash
dotnet build Forge.App
```

```bash
dotnet publish Forge.App -c Release -r win-x64 -p:Platform=x64 -o publish/win-x64
```

The publish output is self-contained and unpackaged, so it runs without installing .NET or the Windows App SDK.
It is about 230 MB, because it carries both runtimes.

### Testing without touching your install

**Debug builds use a separate `dev` profile.** A build from `dotnet build` (or Visual Studio's
Debug) keeps its settings and log in `%LOCALAPPDATA%\ForgeForWindows-dev`, and its tokens in
Credential Manager as `ForgeForWindows-dev:<account>`. It runs beside the installed app instead of
handing its launch over, never writes the "Start with Windows" entry, and says "(dev)" in its titles
and tray tooltip. Add a token to it once. `FORGE_PROFILE` picks another profile, or `default` to run a
debug build against your real one.

**Windows Sandbox tries the installer on a clean Windows** that is wiped when you close it:

```powershell
powershell -ExecutionPolicy Bypass -File tools\sandbox.ps1 -Build
```

`-Build` publishes and compiles a fresh installer first, and without it the newest `dist\*-x64-setup.exe`
is used. `-Portable` runs `publish\win-x64` without installing, and `-FromGitHub` installs the latest
published release, exactly what a new user gets. Windows 11 Pro and Enterprise include Sandbox but
leave it off: turn it on in *Turn Windows features on or off* > *Windows Sandbox*.

**Use a token without write scopes for testing**, in the dev profile and in Sandbox alike. Isolation
keeps your machine clean, but it still talks to your real Forge account, so a read-only token is
what makes a stray click unable to deploy or restart anything.

| Variable | Effect |
|---|---|
| `FORGE_PROFILE` | which profile a run uses: `dev` by default in debug builds, `default` (the real one) in release builds |
| `FORGE_API` | API base URL (default `https://forge.laravel.com/api`), e.g. a local mock |
| `FORGE_STATE_DIR` | folder for `state.json`, so a test run leaves your real state alone |
| `FORGE_SNAPSHOT_DIR` | the flyout writes `panel.png` of its own XAML after each render. Screen capture shows WinUI windows as black. |
| `FORGE_TEST_TOAST` | debug builds: `done` or `failed` shows a sample deployment toast for the first listed site |
| `FORGE_TEST_TOAST_ACTION` | debug builds: plays one of that toast's buttons (`open`, `log`, `forge`), since Windows ignores scripted clicks on toasts |

### Layout

- `Forge.Core/` has no UI.
  - `Model/` holds pure shaping of Forge's JSON:API answers, status precedence, the action tables, paths and text guards.
  - `Api/` holds the HTTP client and the rate budget.
  - `Service/ForgeService` is the poller, notifications, reads, writes and the command/recipe run watch.
  - `Setup/` holds the saved state and `AppProfile`, which keeps a dev profile's data apart.
- `Forge.Core.Tests/` has xUnit tests: model rules, plus the service against a fake client.
- `Forge.App/` is the WinUI 3 shell: the tray icon (H.NotifyIcon), the flyout, the expanded window,
  settings and toasts.
- `tools/install.ps1` and `uninstall.ps1` install from source; `installer.iss` is the Inno Setup installer
  the release workflow builds; `sandbox.ps1` tries a build in Windows Sandbox; `make-icon.ps1`
  regenerates `Forge.App/Assets/forge.ico`.

## License

MIT — see [LICENSE](LICENSE).
