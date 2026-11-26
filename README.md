# Forge for Windows

A tray app for watching and managing Laravel Forge servers on Windows. It's a port of
[omarchy-forge](https://github.com/acobrerosf/omarchy-forge), the Omarchy bar widget, to C# / WinUI 3.

- **Tray icon** with one badge for the worst state across every watched organization:
  none, hollow (maintenance), pulsing (deploy running), amber (token or request problem),
  red (server unreachable or deploy failed). The tooltip summarises it.
- **Flyout** (left-click the tray icon): servers and their sites, with search, organization tabs and
  chips for anything failed, deploying or in maintenance. Hover a site for Deploy and Open site, or a
  server for its page (›), events and ssh; right-click either for everything else. Clicking a server folds
  it. Works fully from the keyboard too.
  - Site: deploy, maintenance mode, quick deploy on or off, copy the deploy hook URL, PHP-FPM reload, run a
    command, heartbeats, open site, open in Forge, copy ssh, open ssh in Windows Terminal, and six sections:
    **Deployments** (every deployment and its log), **Logs** (application, nginx error, nginx access; clear them
    too), **Scheduled jobs** (with the last run's output), **Config files** (opens the window's Config tab),
    **Deploy settings** (opens the window's Deploy tab) and **SSL certificates** (opens the window's SSL tab).
  - Server: restart nginx, PHP-FPM, supervisor, redis and the database (only the services the server type
    runs), reboot, recipes, monitors, and four sections: **Events**, **Logs** (nginx, PHP, database, redis,
    ssh… only the ones the server can have; clear them too), **Background processes** (status and log;
    restart one) and **Scheduled jobs**. **SSH keys** and **Firewall** open the window's tabs of the same name.
- **Window** (the flyout's ⧉ button, Ctrl+O, or the tray menu): the same servers and sites with room to read.
  Each section is one tab laid out the same way: the list on the left, and beside it the output of the
  chosen item, with find, wrap, copy and save. A tab opens on its first item (the newest deployment or
  event), so there is always something to read.
- **Config** (a site's tab in the window): the environment file, the deploy script and the nginx config.
  Each opens read-only; **Edit** is a separate mode, and **Save** (or Ctrl+S) shows a diff of what changes
  and asks first. Forge's copy is read again just before saving, and if it changed since you opened it
  (someone saved it on the website, say), nothing is overwritten. After a save the tab shows what Forge
  now holds. The environment file's values are masked until you press **Reveal**, there's no "save to
  Downloads" for any of the three, and copying the environment file keeps it out of clipboard history.
  Line endings stay as Forge sent them.
- **Deploy** (a site's tab in the window): turn quick deploy (push to deploy) on or off; copy or regenerate
  the deploy hook URL; add, copy and delete deployment webhooks (the URLs Forge calls after a deploy); and
  show, copy, create or delete the site's deploy key. The hook URL is a secret, since anyone holding it can
  deploy: it is never shown, only copied, and kept out of clipboard history. Webhook URLs are shown by host
  only, because they often carry a token. Regenerating the hook asks first and says that anything calling
  the old URL (CI) stops working.
- **SSH keys** (a server's tab in the window): the keys added through Forge, each with its server user and
  status, and **Add my key**: pick one of the `.pub` files in `%USERPROFILE%\.ssh`, choose a `.pub` file
  anywhere else, or paste a public key; name it (your machine's name to start with) and choose the server user (`forge` to start with). Only
  `.pub` files are ever read, and a pasted private key is refused. Once the key is added, **Open ssh in
  Windows Terminal** signs you in. The tab also shows the server's own public key, with Copy.
- **Firewall** (a server's tab in the window): the rules, and adding one (name, port or `from:to` range,
  allow or deny, and the addresses it applies to, or any). **Look up my IP** asks api.ipify.org for your
  public address, but only when you press it, and only fills the box so you can check it. Every
  confirmation names the port and the address, and removing a rule for the server's ssh port, or blocking
  it, warns that it can lock you out.
- **SSL** (a site's tab in the window): each of the site's domains with its active certificate's state.
  A failed or pending certificate request is listed first: Let's Encrypt renews by itself, so a failed
  request is the one to fix. Read only when the tab opens.
- **Nothing extra on the poll.** History, logs, processes, jobs, keys, firewall rules and certificates are read only when you open them.
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
| `q` | quick deploy on or off, on a site's page (asks first) |
| `u` | copy the site's deploy hook URL (kept out of clipboard history) |
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

1. Create a token at <https://forge.laravel.com/profile/api> with only the scopes you will use. Don't
   tick "Select all permissions": the app uses nothing beyond the list below. Settings shows the same
   list under **Which scopes?**, and a view whose scope is missing says which one it needs.

   | Scope | Needed? | What it turns on |
   |---|---|---|
   | `user:view` | **needed** | checking the token: who it belongs to |
   | `organization:view` | **needed** | listing your organizations |
   | `server:view` | **needed** | watching servers and sites; events, logs, processes, jobs, SSH keys and firewall rules |
   | `site:manage-deploys` | optional | deploy, deployment logs, the deploy script, quick deploy, the deploy hook, webhooks and the deploy key |
   | `site:manage-commands` | optional | run commands, and maintenance mode |
   | `site:manage-environment` | optional | read and edit the environment file |
   | `site:manage-nginx` | optional | read and edit the site's nginx config |
   | `site:manage-ssl` | optional | read certificates on the SSL tab, if Forge asks for it |
   | `server:manage-services` | optional | restart nginx, PHP-FPM, supervisor, redis and the database, and reboot |
   | `server:manage-logs` | optional | read and clear site and server logs |
   | `server:create-daemons` | optional | read a background process's log and restart it |
   | `server:create-keys` | optional | add SSH keys |
   | `server:delete-keys` | optional | remove SSH keys |
   | `server:manage-network` | optional | add and remove firewall rules |
   | `recipe:view` | optional | list recipes |
   | `recipe:manage` | optional | run recipes |

2. Start the app. On first run Settings opens. Paste the token, press **Check token**, pick the
   organizations to watch, then press **Save account**.

What is stored where:

- **Tokens:** Windows Credential Manager, under Generic credentials named `ForgeForWindows:<account>`.
  They are never written to a file.
- **Everything else** (accounts, organizations, settings): `%LOCALAPPDATA%\ForgeForWindows\state.json`.
- **Config files** (the environment file above all): never written to disk or to the app's log. They are
  held in memory only while the Config tab shows them.
- **Deploy hook and webhook URLs:** never written to disk or to the app's log. The hook URL is read when
  you copy it and goes straight to the clipboard.
- **SSH keys:** the app reads only the `.pub` files at the top of `%USERPROFILE%\.ssh`, when the SSH keys
  tab opens, and a `.pub` file you choose yourself. It never opens a private key file, and never stores what it read.
- **Your public IP:** looked up from api.ipify.org only when you press **Look up my IP**. It is not stored or logged.
- **"Start with Windows":** `HKCU\...\Run\ForgeForWindows`. The app then starts with `--background`.

## Rate limit

Forge allows 60 requests a minute per Forge user, shared with your browser. The app keeps itself to 40.

- Each refresh costs **2 requests per organization**: the server list, then one window of the organization's site list.
- Past 150 sites, the site list is checked in rotation.
- While a deploy runs, the app looks every 15 seconds instead of at the normal interval.
- A 429 response pauses every organization behind that Forge user until Forge's reset time.
- A config file costs 1 request when opened. A save costs 3: the check that it hasn't changed, the save, and reading it back.
- The Deploy tab costs 2 requests when opened (webhooks and the deploy key). Copying the deploy hook URL costs 1.
  Quick deploy's state comes with the site list, so it costs nothing.
- The SSH keys tab costs 2 requests when opened (the keys and the server's own key, which is read once per visit
  to the server, and not asked again when that read fails). After that it costs 1. The Firewall tab costs 1.
  After a key or rule is added or removed the tab reads again, and while one is still installing it looks again
  up to 3 times, 8 seconds apart. Only a write starts those extra looks; opening a tab never does.
- The SSL tab costs 1 request for the domains plus 1 per domain, for up to 10 domains; **Check more** reads the next 10,
  and fetches the next page of domains (1 request per 30) once the first page is done. A check that Forge refuses
  (a missing scope or a rejected token) or that is held back for the rate limit stops the batch; **Check more** retries
  the domains it didn't reach. Certificates are never checked on the poll.

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
