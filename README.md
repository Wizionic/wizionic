# Wizionic

Privacy-first, local-first AI workspace: chat, notes, gallery, and calendar on your device, with optional local models (Ollama, AMD Lemonade) and optional cloud providers.

<p>
  <a href="https://github.com/Wizionic/wizionic/releases/latest"><img alt="Download" src="https://img.shields.io/github/v/release/Wizionic/wizionic?label=Download"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-blue.svg"></a>
</p>

**Chat, notes, gallery, and calendar stay on the device** (AES-256-GCM at rest). The hosted service is auth, presence, WebRTC signaling, and optional proxies — not a chat archive. See [ARCHITECTURE.md](ARCHITECTURE.md).

This repository is public so you can inspect the product. **It is not a call for contributors.** Forks are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Download

Installers and in-app updates are published to [GitHub Releases](https://github.com/Wizionic/wizionic/releases/latest). The hosted site [wizionic.com](https://wizionic.com) is for login, sync signaling, and the web client.

| Platform | Package |
|---|---|
| Windows 10 / 11 | `Wizionic-win-Setup.exe` (Velopack) |
| Linux (x64) | AppImage, `.deb`, or `curl -fsSL https://github.com/Wizionic/wizionic/releases/latest/download/install.sh \| bash` |

## Sole author

**Daniel Goodwin** — sole developer. Built with AI-assisted development; architecture and product decisions are mine.

- Site: [wizionic.com](https://wizionic.com)
- Email: daniellgoodwin@protonmail.com
- Source: [github.com/Wizionic/wizionic](https://github.com/Wizionic/wizionic)

## Quick start

### Use an installer

Download the latest Windows Setup or Linux AppImage / `.deb` from [Releases](https://github.com/Wizionic/wizionic/releases/latest) or [wizionic.com](https://wizionic.com).

### Run from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet restore App.sln
dotnet run --project App.csproj
```

Open the printed `https://localhost:…` URL. Guest mode works without an account.

### Self-host

See [docs/self-host.md](docs/self-host.md) for Docker and `dotnet run` without any production-server details.

## What stays local vs what the server sees

| On the hosted service | Stays on the device |
|---|---|
| Email, optional password hash, magic-link token | Chat, notes, gallery, calendar **bodies** (encrypted at rest) |
| Auth cookie (`AppAuth`) | User OAuth access tokens and MCP tokens |
| Per-user local encryption key (so your devices can share one key) | Guest data (`wasmchat-` namespace) |
| Optional user-saved cloud API keys, if you chose server storage | |
| OAuth *app* client IDs/secrets | |
| SignalR presence + WebRTC **signaling** (not chat payloads) | |
| Tool-proxy and proxied model calls you choose to use | |

Full statement: [Privacy Policy](docs/privacy.md) · live page: [wizionic.com/privacy](https://wizionic.com/privacy)

## Targets

One codebase, three delivery targets:

- **Host** — ASP.NET Core Blazor app at wizionic.com or a self-hosted homeserver
- **WASM** — browser client with encrypted IndexedDB
- **Desktop** — Windows (MAUI / Velopack) and Linux (GirCore + AppImage / `.deb`)

## Support

Best-effort, no SLA. Confirmed bugs on the latest release can be filed with the bug template. Security reports go through [SECURITY.md](SECURITY.md).

## License

[Apache License 2.0](LICENSE). See [NOTICE](NOTICE).

## Documents

- [Privacy Policy](docs/privacy.md)
- [Terms of Service](docs/terms.md)
- [Security policy](SECURITY.md)
- [Changelog](CHANGELOG.md)
- [Architecture](ARCHITECTURE.md)
- [Self-host](docs/self-host.md)

## Code signing policy

Free code signing will be provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org/), after the first unsigned GitHub Release is verified and the SignPath application is approved.

Until then, GitHub Release installers are **unsigned**. Windows SmartScreen may warn on first launch.

**Roles:** Daniel Goodwin is Author, Reviewer, and Approver.

The desktop app does not transfer information to other networked systems unless you ask it to (sign in, sync signaling, user-configured models and connectors, or an update check). See the [Privacy Policy](docs/privacy.md).
