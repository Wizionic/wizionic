# Self-host Wizionic

This guide runs the **host** (Blazor web app + APIs + SignalR) on your own machine or a VPS. It does not describe Wizionic's production server.

Chat, notes, gallery, and calendar still live on each client. The host stores accounts, Data Protection keys, optional saved provider keys, and signaling.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) to run from source
- Or [Docker](https://docs.docker.com/get-docker/) to run the published image
- Optional: [Ollama](https://ollama.com) on the same machine or LAN

## Run from source

```bash
git clone https://github.com/Wizionic/wizionic.git
cd wizionic
dotnet restore App.sln
dotnet run --project App.csproj
```

The development server prints `http`/`https` URLs. `appsettings.Development.json` is a template: empty OAuth secrets, localhost OAuth redirect URIs, local SQLite at `data/homeserver.db`.

### Environment variables (production-style)

Secrets belong in the environment, not in git.

| Variable | Purpose |
|---|---|
| `BREVO_API_KEY` | Transactional email (magic links). Leave unset to skip email login. |
| `Email__SmtpUser` / `Email__SmtpPass` | Only if you switch back to SMTP |
| `Twilio__AccountSid` | Twilio Account SID (`AC…`). Required only for SMS 2FA. |
| `Twilio__ApiKeySid` | Twilio API Key SID (`SK…`). Prefer this over the account auth token. |
| `Twilio__ApiKeySecret` | Twilio API key secret. |
| `Twilio__VerifyServiceSid` | Twilio Verify Service SID (`VA…`). Create a service named Wizionic in the Twilio console. |
| `ZYPHRA_API_KEY` | Optional proxied Zyphra models |
| `ConnectionStrings__DefaultConnection` | Override SQLite path |

OAuth *app* Client IDs and secrets (Google, GitHub, Notion, Stripe) live in the host SQLite `OAuthProviders` table, not in deploy-time environment variables. Seed them in the database (the production notes live in the private ops repo). Empty `OAuth:*` entries in `appsettings.json` are only a local-dev fallback.

The sample `free-chat` proxied provider in `appsettings.json` points at `http://127.0.0.1:11434/v1/` (local Ollama). Change or remove it if you do not run Ollama there.

SMS two-factor is optional. If the Twilio variables are unset, users can still enable 2FA and complete the second step with an email code. Do not put the Twilio auth token in config; use an API key.

## Docker

From the repo root:

```bash
docker build -t wizionic-host .
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e BREVO_API_KEY= \
  -v wizionic-data:/app/data \
  wizionic-host
```

Then open `http://localhost:8080`. Persist `/app/data` so the SQLite database and Data Protection keys survive container replacement.

The `Dockerfile` builds the host + WASM client only. It does not build the Windows/Linux desktop apps.

## Desktop clients

Desktop installers are built by GitHub Actions (see `.github/workflows/release.yml`) and published to [GitHub Releases](https://github.com/Wizionic/wizionic/releases/latest). Point a desktop client at your host by using that host as the account / sync server in the app.

To publish a desktop build yourself:

```bash
# Windows (on Windows)
dotnet publish App.Maui/App.Maui.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained

# Linux (on Linux)
dotnet publish App.Maui/App.Maui.csproj -c Release -f net10.0 -r linux-x64 --self-contained
```

Packaging into a Velopack Setup.exe / AppImage is handled by `scripts/pack-windows.ps1` and `scripts/pack-linux.sh`.

## What this host will not do

- It will not store WASM/MAUI chat history on the server. That is an architectural boundary.
- It will not include Wizionic production deploy scripts. Those live in a private ops repo.
