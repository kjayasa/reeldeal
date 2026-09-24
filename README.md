# Introduction 
Web site for kagw Reel deal

# How does it work
the base of teh site is a bootstrap template I bought.
It uses really oudated js libs.
I kinda upgraded it to use webpack. 

The main index is handle bar template.

A helper called **data** is used to pull in sections of the site from the **/data/** folder.
sectiond are sored as markdown files.

## For example,
The tearms and rules secton is read from a file /data/terms-condition.md.

using

    {{{data "terms-condition"}}}

in index.hbs pulls /data/terms-condition.md and renders it inside the index html's tearms and ruls model.

The side is rendered to its final for at build buy running

    npm run build

It publishes the site to ./public folder.

Any push to main kicks of an Azure Dev Ops pipeline that build the site and deploys it.

# Runtime CMS

The site can now be edited at runtime (no rebuild) through a small admin UI backed by SQLite.

## How it works
* Content (the `*.md` sections) and variables (formerly `data.json`) live in a SQLite database (`cms.db`),
  accessed with **Dapper**. The connection string is `ConnectionStrings:Cms` in `appsettings.json`.
* `cms.db` is committed to the repo as the **seed/starting state** and copied next to the app on build
  (`CopyToOutputDirectory=PreserveNewest`).
* The page template itself (formerly `public/index.hbs`) is stored in the `Templates` table as `index`.
  Content blocks are inserted with `{{{ContentBlocks "key"}}}`, and blocks may use template variables
  (e.g. `{{submissionPrice_late}}`) and other helpers too.
* On startup the app creates the schema if missing and seeds content/variables/templates from `/data/*.md`,
  `/data/data.json` (nested keys flattened with `_`) and `/data/*.hbs` the first time each is missing. After that, the database is the source
  of truth and `/data` is just the seed source.
* The home page is rendered from the database per request and cached; an admin save invalidates the cache so
  changes appear immediately without a restart.

## Admin UI
`/admin` is a single-page editor with four tabs:
* **Content** — each content block with a live Markdown preview, Save and Delete buttons, plus a form to add new blocks.
* **Page template** — the whole home page Handlebars template.
* **Variables** — one row per template variable (name, value, description); use as `{{name}}`. The same set is
  exposed to the site's JavaScript as `window.reeldeel_data`.
* **Admins** — the accounts that can sign in (see below).

Saving invalidates the render cache, so changes show on the public site immediately. Every save is
trial-rendered first; a template error is rejected with the message and the live site is left unchanged.

# Authentication

Access to `/admin` is protected by a plain **email + password** login. There is no social/OAuth login and
no self-registration: an existing admin (or a maintenance command) must create every account.

## How it works
* Sign in at `/admin/login`. Unauthenticated requests to `/admin` or `/admin/api/*` are redirected there.
* Accounts live in the `Users` table of `cms.db`. Passwords are stored as **salted PBKDF2-SHA256 hashes**
  (210,000 iterations); the plain text is never written anywhere.
* A successful login issues an HttpOnly, SameSite=Lax cookie (`reeldeel.admin`) that lasts 12 hours with
  sliding expiration. Ticking **Stay signed in** makes it persist across browser restarts.
* Every request re-checks that the cookie's email still exists in `Users`, so removing an admin locks them
  out on their next request. They land on `/admin/denied` with a Sign out button.
* Passwords must be at least 8 characters. Failed logins get a generic "Incorrect email or password"
  message, and unknown emails take the same time to reject as wrong passwords.
* A user can exist **without** a password (e.g. accounts migrated from the old allow-list, or added without
  one). They can't sign in until an admin sets a password for them.
* **Sign out** is the button in the admin header (`POST /admin/logout`).

## First-time setup
Fresh databases have no password, so nobody can sign in. The app prints a warning at startup when that is
the case. Pick **one** of these to create the first admin:

1. **Reset the seed database locally (recommended for this repo).** Run the helper, commit `cms.db`, deploy:
   ```powershell
   .\tools\set-admin.ps1 you@example.com     # Windows
   ./tools/set-admin.sh you@example.com      # bash / macOS / Linux
   ```
   It prompts for the password twice (hidden input) and pipes it to `dotnet run -- --reset-admin`, so the
   password never appears on the command line or in shell history. **All other admin accounts are removed**
   so the seed holds exactly one known login. Add colleagues back through the Admins tab afterwards.

2. **Bootstrap from configuration (good for App Service).** `appsettings.json` contains:
   ```jsonc
   "Cms": {
     "InitialAdmin": { "Email": "you@example.com", "Password": "" }
   }
   ```
   Leave `Password` blank in the repo and set it as an App Service application setting / environment
   variable named `Cms__InitialAdmin__Password`. On startup, **only while no user has a password**, that
   account is created (or updated) with the given password. Once anyone has a password the setting is
   ignored, so delete it after the first login.

3. **Set a single password directly** (also the lockout recovery path):
   ```bash
   dotnet run -- --set-password you@example.com
   ```
   You'll be prompted for the password (or pass it as a third argument). The account is created if it
   doesn't exist; other accounts are untouched.

## Managing admins (Admins tab)
* **Add admin** — enter an email and, optionally, an initial password. Without one the account shows
  *not set* and can't sign in yet.
* **Set / Reset password** — per-row button; any admin can reset any other admin's password. Share the new
  password out-of-band and ask them to change it.
* **Change my password** — the second card on the tab; enter the new password twice.
* **Remove** — deletes the account and signs them out on their next request. The **last account that can
  sign in cannot be removed**, so you can't lock everyone out from the UI.

## Locked out?
If nobody can sign in (forgotten password, deleted account, restored database):
```bash
dotnet run -- --set-password you@example.com
```
Run it in the project directory against the same database the app uses (`ConnectionStrings:Cms`). On
Azure App Service you can instead set `Cms__InitialAdmin__Email` / `Cms__InitialAdmin__Password` and restart,
provided no other account still has a password; otherwise use the Kudu console to run the command above.

## Maintenance commands
All of these run against the database in `ConnectionStrings:Cms` and exit without starting the web server.
If `[password]` is omitted it is read from stdin (first line), which keeps it out of your shell history.

| Command | What it does |
| --- | --- |
| `dotnet run -- --init-db` | Create/migrate the schema and seed content from `/data`. |
| `dotnet run -- --set-password <email> [password]` | Create the admin if needed and set their password. |
| `dotnet run -- --reset-admin <email> [password]` | Delete **all** admins and leave exactly this one. |
| `dotnet run -- --help` | Print this list. |
| `tools/set-admin.ps1` / `tools/set-admin.sh <email>` | Interactive wrapper around `--reset-admin` with a hidden, confirmed password prompt. |

Databases created by the previous social-login version are migrated automatically on startup: the old
`AllowedUsers` emails are copied into `Users` without passwords, and the old table is dropped.

The old single-JSON `Variables` table is likewise converted on startup to one row per variable (nested keys
flattened with `_`, e.g. `submissionPrice.late` -> `submissionPrice_late`).

## Security notes
* Serve `/admin` over **HTTPS** in production; the cookie is marked Secure whenever the request is HTTPS.
* There is no rate limiting or account lockout on the login form. If the admin URL is exposed to the
  public internet, choose long passwords or put the site behind an IP restriction / App Service
  authentication front door.
* Never commit a `cms.db` whose password you've shared widely; anyone with the repo can log in to a fresh
  deploy. Rotate the password from the Admins tab after the first deploy.

## Deployment note
Because `cms.db` is committed and copied to output with `PreserveNewest`, a deploy that ships a **newer**
`cms.db` will overwrite content edited live in production. Treat the committed `cms.db` as the seed; do
content edits through `/admin` (which writes to the deployed copy), and avoid re-committing `cms.db` unless
you intend to reset prod content. On Azure App Service, point `ConnectionStrings:Cms` at a path under the
persistent `/home` directory to keep runtime edits across restarts/deploys.
