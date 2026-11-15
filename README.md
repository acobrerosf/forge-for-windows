# Forge for Windows

A tray app for watching and managing Laravel Forge servers on Windows. It's a port of
[omarchy-forge](https://github.com/acobrerosf/omarchy-forge), the Omarchy bar widget, to C# / WinUI 3.

- **Tray icon** with one badge for the worst state across every watched organization:
  none, hollow (maintenance), pulsing (deploy running), amber (token or request problem),
  red (server unreachable or deploy failed). The tooltip summarises it.
- **Panel** (left-click the tray icon): servers and their sites, keyboard first.
  - Site actions: deploy, maintenance mode, PHP-FPM reload, run a command, deployment log,
    application and nginx logs, heartbeats, open site, open in Forge, copy ssh, open ssh in Windows Terminal.
  - Server actions: restart nginx, PHP-FPM, supervisor, redis and the database (only the services the
    server type runs), reboot, events, recipes, monitors.
- **Toasts** when a deployment finishes or fails. Clicking one opens the site.
- **Every write arms first** and is sent only by a capital **Y**. Any other key, or 8 seconds passing, cancels it.

## Keys

| Key | Does |
|---|---|
| `↑` `↓` / `j` `k` | move |
| `Enter` | fold a server, open a site, choose an action |
| `→` / `l` | drill in (a server's actions) |
| `←` / `h` / `Backspace` | back |
| `Esc` | back, then close |
| `d` | arm a deploy of the current site |
| `Y` | send what is armed |
| `o` / `f` / `s` / `t` | open site / open in Forge / copy ssh / ssh in Windows Terminal |
| `e` | server events |
| `r` | refresh (or reload the open log) |
| `c` / `w` | in a log: copy / save to Downloads |
| `,` | settings |

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

### Development switches

| Variable | Effect |
|---|---|
| `FORGE_API` | API base URL (default `https://forge.laravel.com/api`), e.g. a local mock |
| `FORGE_STATE_DIR` | folder for `state.json`, so a test run leaves your real state alone |
| `FORGE_SNAPSHOT_DIR` | the panel writes `panel.png` of its own XAML after each render. Screen capture shows WinUI windows as black. |
