# Forge for Windows

A tray app for watching and managing Laravel Forge servers on Windows. It's a port of
[omarchy-forge](https://github.com/acobrerosf/omarchy-forge), the Omarchy bar widget, to C# / WinUI 3.

- **Tray icon** with one badge for the worst state across every watched organization:
  none, hollow (maintenance), pulsing (deploy running), amber (token or request problem),
  red (server unreachable or deploy failed). The tooltip summarises it.
- **Flyout** (left-click the tray icon): servers and their sites, with search, organization tabs and
  chips for anything failed, deploying or in maintenance. Hover a row for Deploy and Open site, or
  right-click it for everything else. Works fully from the keyboard too.
  - Site actions: deploy, maintenance mode, PHP-FPM reload, run a command, deployment log,
    application and nginx logs, heartbeats, open site, open in Forge, copy ssh, open ssh in Windows Terminal.
  - Server actions: restart nginx, PHP-FPM, supervisor, redis and the database (only the services the
    server type runs), reboot, events, recipes, monitors.
- **Window** (the flyout's ⧉ button, Ctrl+O, or the tray menu): the same servers and sites with room to read.
  A site's logs open in tabs with find, wrap, copy and save; a server's events sit beside their output.
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
| `e` | server events |
| `r` | refresh (or reload the open log) |
| `c` / `w` | in a log: copy / save to Downloads |
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
   to watch them. Each write scope is opt-in: `site:manage-deploys`, `server:manage-services`,
   `site:manage-commands`, `server:manage-logs`, `recipe:view` and `recipe:manage`.
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

### Layout

- `Forge.Core/` has no UI.
  - `Model/` holds pure shaping of Forge's JSON:API answers, status precedence, the action tables, paths and text guards.
  - `Api/` holds the HTTP client and the rate budget.
  - `Service/ForgeService` is the poller, notifications, reads, writes and the command/recipe run watch.
- `Forge.Core.Tests/` has xUnit tests: model rules, plus the service against a fake client.
- `Forge.App/` is the WinUI 3 shell: the tray icon (H.NotifyIcon), the panel, settings and toasts.
- `tools/make-icon.ps1` regenerates `Forge.App/Assets/forge.ico`.

## License

MIT — see [LICENSE](LICENSE).

### Development switches

| Variable | Effect |
|---|---|
| `FORGE_API` | API base URL (default `https://forge.laravel.com/api`), e.g. a local mock |
| `FORGE_STATE_DIR` | folder for `state.json`, so a test run leaves your real state alone |
| `FORGE_SNAPSHOT_DIR` | the panel writes `panel.png` of its own XAML after each render. Screen capture shows WinUI windows as black. |
