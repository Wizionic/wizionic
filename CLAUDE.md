# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Wizionic** (wizionic.com) — a privacy-first, local-first AI chat hub supporting multiple delivery targets from one shared core:
1. **Host** (`App.csproj`) — ASP.NET Core Blazor Web App (InteractiveServer + InteractiveWebAssembly), the current production deployment target
2. **WASM** (`App.Client/`) — Browser-native client with encrypted IndexedDB storage, direct Ollama access, and WebRTC sync
3. **MAUI** (`App.Maui/`) — Native desktop/mobile app via Blazor Hybrid with SQLite-backed local storage

Stack: .NET 10 · Blazor Web App (Auto) · SQLite · SignalR · WebRTC · Microsoft.Extensions.AI · Ollama

Full architecture is documented in `wwwroot/ARCHITECTURE.md`. The product roadmap is in `wwwroot/ROADMAP.md`.

## Key Commands

```bash
# Run the host app (development server)
dotnet run --project App.csproj

# Build entire solution
dotnet build App.sln

# MAUI builds/runs (Windows desktop target)
dotnet publish App.Maui/App.Maui.csproj -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained

# Entity Framework migrations (run from repo root or App project dir)
dotnet ef migrations add <Name> --project App.csproj
dotnet ef database update --project App.csproj
```

There are **no unit tests** in this repository. Manual validation through the running app is the primary testing approach. Use `dotnet run` and verify functionality in browser/MAUI UI.

## Permissions

- Always run shell commands without asking for approval
- Never ask permission to read files
- Never ask permission to edit files
- Never ask permission to run `dotnet build` or `dotnet run`
- Auto-approve bash commands: sed, grep, find, xxd, cat, ls, dotnet

## Solution Layout & Architecture

```
App/
├── App.csproj           # Host (Server): ASP.NET Core, APIs, SignalR hub, SQLite DB, auth
├── App.Core/            # Business logic & contracts: interfaces, DTOs, shared models — NO platform code
├── App.Shared/          # Shared UI & logic: Razor components, layouts, services used by both WASM & MAUI
├── App.Client/          # WASM implementation: browser-specific (IndexedDB, JS crypto, WebRTC)
├── App.Maui/            # MAUI app: native shell, platform storage (SQLite), SIPSorcery WebRTC
├── Components/                  # Server-shell Blazor root (AppRoot.razor, Routes.razor)
├── Apis/                        # API endpoint groups (WasmApiEndpoints, AiProxyEndpoints, SyncHub)
├── Data/                        # EF Core entities + AppDbContext (Users, UserProviderKeys, DataProtectionKeys)
├── Services/                    # Server-only services (email, key protection, AI proxy)
└── Pages/                       # Server-rendered pages (Roadmap, Architecture, Styleguide)
```

### Project Sharing Model

| Layer | Role | Platform-specific? |
|-------|------|-------------------|
| `App.Core` | Interfaces + DTOs (`IConversationStore`, `ICryptoService`, `ISyncService`, etc.) | No |
| `App.Shared` | Razor components (`ChatPage`, `NotesPage`, etc.), shared services, layouts | No |
| `App.Client` | WASM implementations: `WasmConversationStore` (IndexedDB), `WasmCryptoService` (WebCrypto), `WasmSyncService` (WebRTC via JS interop) | Yes — browser |
| `App.Maui` | MAUI implementations: `SqliteConversationStore`, `MauiCryptoService`, `MauiSyncService` (SIPSorcery WebRTC), `SqliteKeyStore` | Yes — native |

### Key Files Reference

**Startup & routing:**
- `Program.cs` — app builder: Blazor modes, EF Core SQLite, cookie auth (10-year sliding), SignalR hub, forwarded headers, magic-link routes
- `App.Client/Program.cs` — WASM DI setup + service registrations
- `App.Maui/MauiProgram.cs` — MAUI app initialization

**APIs (in `Apis/`):**
- `WasmApiEndpoints.cs` — `/api/auth/*`, `/api/user/encryption-key`, `/api/keys`, `/api/tools/*`
- `AiProxyEndpoints.cs` — `/api/proxy/providers`, `/api/proxy/chat` for CORS-restricted cloud models

**Shared UI components (in `App.Shared/Components/`):**
| Route | Component | Purpose |
|-------|-----------|---------|
| `/` | `LoginPage.razor` | Landing, magic-link login, guest continue |
| `/chat` | `ChatPage.razor` | Chat UI, sidebar, attachments, tool traces |
| `/notes` | `NotesPage.razor` | Notebooks, Quill editor entries |
| `/sync` | `SyncPresencePage.razor` | Device list, sync targets, AI server pick |
| `/local-ai` | `LocalAiPage.razor` | Ollama URL, model discovery, vision proxy config |
| `/cloud-providers` | `CloudProvidersPage.razor` | API keys for cloud models |
| `/settings` | `SettingsPage.razor` | Profile, system prompt, preferences |
| `/tools` | `ToolsPage.razor` | MCP servers and tokens |

**Shared services (in `App.Shared/Services/`):**
- `ChatCompletionService.cs` — core AI completion loop + ME.AI function calling
- `ChatModelCatalogService.cs` — available model catalog across providers
- `Mcp/McpToolSource.cs` — MCP tool discovery and caching
- `Tools/AppTools.cs` — built-in tools (`search_web`, `summarize_url`, `get_time`)
- `QuillInterop.cs` — Quill rich text editor JS interop

**Core interfaces (in `App.Core/`):**
- `Storage/IConversationStore.cs` — chat history persistence contract
- `Storage/INoteStore.cs` — notes persistence contract
- `Storage/ICryptoService.cs` — AES-GCM encryption/decryption contract
- `Storage/IKeyStore.cs` — settings/keys storage contract
- `Sync/ISyncService.cs` — cross-device sync contract
- `Auth/IAuthService.cs` — auth session contract

**Data layer:**
- `Data/AppDbContext.cs` — EF Core context (Users, UserProviderKeys, DataProtectionKeys)
- `Migrations/` — 12 migrations tracking the database evolution

## Architecture Principles & Conventions

### The "Local-First" Rule
When modifying WASM or MAUI code: **chat history and note content must NEVER be stored on the central server**. Only auth, presence, and small WebRTC signaling messages touch the server. This is a hard architectural boundary — do not introduce server-side chat storage for these targets.

### Storage Namespace Isolation
- Guest mode: `wasmchat-` prefix in IndexedDB/localStorage
- Authenticated mode: `u-{userId}-` prefix
- The `WasmGuestDataMigrationService` handles the transition from guest to authenticated namespaces on login

### Encryption Model
- Content (message/note bodies) is **AES-256-GCM encrypted** before storage — metadata (titles, dates) remains cleartext for fast sidebar listing
- WASM uses WebCrypto via JS interop (`WasmCryptoService` + `encryptLocalData`/`decryptLocalData`)
- MAUI uses native .NET crypto (`MauiCryptoService`)
- Server encrypts keys at rest with ASP.NET Data Protection (stored in SQLite)

### AI Provider Routing
The model selector groups models by provider. Providers are:
1. **Ollama** — direct to `http://localhost:11434/v1` (first-class, zero-config, no API key needed)
2. **Proxied cloud** (`proxied/*`) — POST `/api/proxy/chat` using server-side keys
3. **User-keyed** — direct calls to Groq, OpenRouter, Gemini, etc. from the client

The **Vision Proxy** system routes image/PDF attachments through a vision-capable model (one per scope) when the selected target model doesn't support vision. Descriptions are injected as text. See `ARCHITECTURE.md` section "Vision Proxy" for full details.

### Sync Architecture
- SignalR hub (`/sync-hub`) handles presence and WebRTC signaling — **never** data payloads
- WebRTC DataChannel carries encrypted JSON (manifest exchange → only changed items sync)
- MAUI uses SIPSorcery (C# WebRTC); WASM uses native `RTCPeerConnection` via JS interop
- AI relay: a device with local Ollama can serve as the "AI server" for other peers over a dedicated DataChannel

### Tool Use Flow
Models call tools via Microsoft.Extensions.AI `UseFunctionInvocation`. Tools come from:
1. **AppTools** (`search_web`, `summarize_url`, `get_time`) — executed on server via `/api/tools/*`
2. **MCP servers** — discovered and cached in `McpToolSource`, called directly from client
3. Traces render as `ToolExecutionTrace` components in the chat UI

### EF Core Migrations
The database stores only: Users (email, magic-link token, local encryption key), UserProviderKeys (optional server-stored API keys), and DataProtectionKeys. Chat history does NOT go here for WASM/MAUI targets. Always add a migration after model changes:
```bash
dotnet ef migrations add <Name> --project App.csproj
```

## Common Workflows by Task Type

### Adding AI/Chat features → `ChatPage.razor` (Shared), `ChatCompletionService` (Shared), `ChatModelCatalogService`
### Storage/privacy changes → Core interfaces (`IConversationStore`, `ICryptoService`) + platform-specific implementations in Client or MAUI
### Vision proxy / model routing → `LocalAiPage.razor`, `ChatCompletionService`, key stores (`WasmKeyStore` / `SqliteKeyStore`)
### New API endpoints → `Apis/WasmApiEndpoints.cs` or `Apis/AiProxyEndpoints.cs`; register in `Program.cs`
### Tools/MCP → `Tools/AppTools.cs`, `McpToolSource` (Shared), `ToolsPage.razor`
### Sync changes → `ISyncService`/`IWebRtcTransport` (Core) + platform implementations + `SyncPresencePage.razor`

## Deployment Notes

Production deploys to an M5 Linux server via Railway-style Docker/container setup. The host project uses forwarded headers for TLS termination behind a reverse proxy. Cookie auth uses 10-year sliding expiration with Data Protection keys persisted in SQLite (survives restarts). Release builds enable trimming and exclude `appsettings.Development.json`.
