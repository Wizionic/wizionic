# AGENTS.md — Wizionic

Single agent-instruction file for this repo. Architecture details live in [`ARCHITECTURE.md`](ARCHITECTURE.md).

## Commands

```bash
dotnet run --project App.csproj          # Run host (Blazor Server/WASM)
dotnet build App.sln                     # Build solution
dotnet ef migrations add <Name>                  # Add EF Core migration
dotnet ef database update                        # Apply pending migrations
dotnet publish App.Maui/ -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained   # MAUI build
```

## Architecture (key facts)

Full write-up: [`ARCHITECTURE.md`](ARCHITECTURE.md) (repo root; not a public site page).

- **Three targets from one codebase**: Host (`App.csproj`), WASM (`App.Client/`), MAUI (`App.Maui/`)
- **Shared layers**: `App.Core/` (interfaces/DTOs, no platform code), `App.Shared/` (Razor components + shared services)
- **Local-first**: Chat history and note bodies are AES-256-GCM encrypted on the client. Only auth metadata/signaling touches the server. Never add server-side chat storage for WASM/MAUI targets.
- **Storage isolation**: Authenticated data uses `u-{userId}-` in IndexedDB/SQLite. Sign-in is required; there is no guest workspace.
- **AI routing**: Ollama → direct to localhost; hosted cloud models → proxied via `/api/proxy/chat` with server-side keys; user-keyed OpenAI-compatible providers (`cloud/{provider}/*`) called directly from the client.
- **Sync**: SignalR hub (`/sync-hub`) for presence + WebRTC signaling only (never data payloads). WebRTC DataChannel carries encrypted JSON. MAUI uses SIPSorcery; WASM uses native `RTCPeerConnection` via JS interop.
- **Workflows**: Device-local schedules (not synced); **Skills** + installed **Tools** do sync.

## Directory map

| Path | Role |
|------|------|
| `App.Core/` | Interfaces + DTOs (IConversationStore, ICryptoService, ISyncService, etc.) |
| `App.Shared/Components/` | Razor pages: Login, Chat, Notes, Sync, LocalAI, CloudProviders, Settings, Tools |
| `App.Shared/Services/` | ChatCompletionService, ChatModelCatalogService, McpToolSource, QuillInterop |
| `App.Client/` | WASM impls: WasmConversationStore (IndexedDB), WasmCryptoService (WebCrypto) |
| `App.Maui/` | MAUI impls: SqliteConversationStore, MauiCryptoService, MauiSyncService, SqliteKeyStore |
| `Apis/` | WasmApiEndpoints.cs (auth/keys/tools), AiProxyEndpoints.cs (cloud model proxy) |
| `Data/` | EF Core context + entities, Migrations/ |

## Git (review before remote)

- Commit locally when a change is complete if that helps the user review history.
- **Do not `git push`, create a GitHub PR, or force-update a remote branch unless the user explicitly says to push or that the work is ready.**
- Leave the working tree in place so the user can inspect and make small edits in Visual Studio / VS Code first.

## UI

Keep settings and setup **short**. Labels and a one-line lead are enough; procedures go in Help (`docs/user/` and the copy in `App.Shared/wwwroot/help/`). Wire a `HelpGlyph` on new headings (and on fields that need steps, like Home Assistant’s long-lived token). Do not duplicate help articles as Razor paragraphs. See **UI copy** in [`ARCHITECTURE.md`](ARCHITECTURE.md).

## Constraints & gotchas

- No unit tests — manual validation via running app is the primary testing approach.
- Framework: .NET 10 · Blazor Web App (Auto render mode) · SQLite via EF Core · SignalR · Microsoft.Extensions.AI
- Cookie auth uses 10-year sliding expiration with Data Protection keys in SQLite (survives restarts).
- Content encryption: metadata (titles/dates) stored cleartext for fast sidebar listing; message/note bodies encrypted before any persistence.
- Add migrations after every model change in Core entities or DbContext.

# Testing & Verification Rules

- Only verify that the project **builds successfully**.
- Do **not** start the application, open a browser, navigate pages, run the server, or perform any runtime / end-to-end / UI testing.
- Do **not** use browser tools, web fetching for testing, or any form of interactive verification.
- After making changes, run the build command (e.g. `dotnet build`) and confirm it succeeds with no errors.
- Leave all testing, manual verification, and browser work to the user.