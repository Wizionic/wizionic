# Chatfish Architecture

**Purpose:** Quick reference for humans and AI agents working on this codebase. Describes what exists today (not the future roadmap). For planned work see [ROADMAP.md](/roadmap).

**Stack:** .NET 10 · Blazor Web App (Auto: server shell + Interactive WebAssembly) · SQLite · SignalR · WebRTC · Microsoft.Extensions.AI

---

## Core Values

- **Privacy-first** — Chat history and notes live in the browser (IndexedDB), encrypted at rest. The server does not store conversation content for the WASM path.
- **Local AI** — Ollama on the user's machine is a first-class provider. A logged-in device can relay AI to other devices over WebRTC.
- **Login is optional** — Guests can chat and take notes immediately. Email + magic link is only needed for cross-device sync and encrypted key distribution.
- **Minimal server footprint** — Server handles auth, signaling, tool proxies (CORS), and CORS-restricted AI proxies. Heavy lifting runs in the browser.
- **Tool-rich agents** — Built-in web search / URL summarization plus user-selected MCP servers, wired through `Microsoft.Extensions.AI` function calling.
- **Low-cost cloud** — Favor free or inexpensive models (proxied providers in `appsettings`, user API keys in browser storage).

---

## Solution Layout
 
 ```
 ChatfishApp/
 ├── ChatfishApp.csproj          # Host (Server): ASP.NET Core, APIs, SignalR hub, SQLite, auth
 ├── ChatfishApp.Core/           # Business Logic & Contracts: Interfaces, DTOs, shared models
 ├── ChatfishApp.Shared/         # Shared UI & logic: Razor components, Layouts, Common services (used by both WASM & MAUI)
 ├── ChatfishApp.Client/         # WASM Implementation: Browser-specific implementations (IndexedDB, JS Crypto)
 ├── ChatfishApp.Maui/           # MAUI App: Native shell, native storage (SQLite), native sync
 ├── Components/                 # Server shell for Blazor Web App (App.razor, Routes.razor)
 ├── Apis/                       # Host API endpoints (WasmApiEndpoints, SyncHub, etc.)
 ├── Data/                       # Server-side EF Core entities + ChatfishDbContext
 ├── Services/                   # Server-only services: email, key protection, AI proxy
 ├── Pages/                      # Server-rendered pages (Roadmap, Architecture)
 └── wwwroot/                    # Static assets and documentation
 ```
 
 ### Project Sharing Model: WASM vs MAUI
 
 | Layer | Shared? | Role |
 |-------|---------|------|
 | **`ChatfishApp.Core`** | ✅ Yes | Defines the "what": Interfaces (`IConversationStore`, `ISyncService`) and DTOs. No platform-specific code. |
 | **`ChatfishApp.Shared`** | ✅ Yes | Defines the "how it looks": Razor components (`ChatPage`, `NotesPage`), Layouts, and logic common to both clients. |
 | **`ChatfishApp.Client`** | ❌ No | WASM-specific: Implements Core interfaces using browser APIs (IndexedDB, WebCrypto). |
 | **`ChatfishApp.Maui`** | ❌ No | MAUI-specific: Implements Core interfaces using native APIs (SQLite, platform storage), hosts the Shared UI in a WebView/Blazor Hybrid shell. |


---

## Authentication & Encryption

### Guest mode
- No cookie. `WasmAuthService` generates a per-browser **guest encryption key** in IndexedDB (`guest-encryption-key`).
- Data namespace: `wasmchat-` (conversations and notes).

### Logged-in mode
- User requests magic link → email via Brevo → `/magic-login?token=...` sets a **persistent** `ChatfishAuth` cookie (survives browser restarts; renewed on activity via sliding expiration; cleared only on explicit sign-out).
- WASM calls `/api/auth/me` and `/api/user/encryption-key` (cookie sent automatically, same origin).
- Per-user **server encryption key** (random, protected at rest in SQLite via ASP.NET Data Protection).
- Data namespace: `u-{userId}-`.
- On login, `WasmGuestDataMigrationService` re-encrypts guest IndexedDB data into the authenticated namespace.

### At-rest encryption
- All conversation and note **content blobs** are AES-256-GCM encrypted before IndexedDB write (`WasmCryptoService` + JS `encryptLocalData` / `decryptLocalData` in `App.razor`).
- Metadata (titles, dates, sync flags) is cleartext for fast sidebar listing.

---

## Chat Flow

```
User types in Chat.razor
        │
        ▼
WasmChatCompletionService.CompleteAsync()
        │
        ├── Build history from WasmConversationStore (decrypted messages)
        ├── Prepend system prompt (profile settings from WasmKeyStore)
        ├── If model supports tools → UseFunctionInvocation (ME.AI)
        │       ├── AppTools (search_web, summarize_url, get_time) via /api/tools/*
        │       └── MCP tools from McpToolSource (user-enabled servers)
        └── WasmAiProviderService.GetChatClientForModel()
                ├── ollama/*     → direct to user's Ollama OpenAI-compat endpoint
                ├── proxied/*    → POST /api/proxy/chat (server-side key)
                └── user keys    → direct to Groq, OpenRouter, Gemini, etc.
        │
        ▼
Response streamed/displayed; WasmConversationStore saves encrypted JSON to IndexedDB
        │
        └── If authenticated + auto-sync on → WasmSyncService queues WebRTC sync
```

**Notes:** Parallel store (`WasmNoteStore`) with Quill HTML entries. Messages can be added from chat via "Add to notes" dialog.

---

## Vision Proxy (image routing for non-vision models)

Many useful local or cloud models are **text-only** but users still want to attach images or PDFs. Vision proxy is a **routing layer** that sends attachments to a designated vision-capable model first, then injects the description as text into the conversation for the model the user actually selected.

### How it works

```
User attaches image/PDF and chats with a text-only model
        │
        ▼
Is selected model vision-capable?
        ├── Yes → image bytes sent directly to that model (normal multimodal path)
        └── No  → is a vision proxy configured?
                    ├── No  → attachment ignored for LLM context
                    └── Yes → vision proxy model describes the attachment(s)
                              → text prefix injected into user message
                              → text-only model receives description, not raw bytes
```

1. User picks **one** vision-capable model as the proxy (only one active at a time).
2. On send, `WasmChatCompletionService.ApplyVisionProxyAsync` finds the latest user message with image/PDF attachments.
3. Each attachment is sent to the proxy model with a describe/summarize prompt.
4. Descriptions are prepended as `[Image context — described by vision proxy model '…']` and **attachments are stripped** from the payload to the target model.
5. A tool-trace line (`👁️ Vision proxy …`) appears in chat so the user sees routing happened.

### Configuration surfaces

| Surface | Where | Purpose |
|---------|-------|---------|
| **Local AI** (`LocalAi.razor`) | Per-Ollama-model editor → **Vision proxy** checkbox | Mark a local vision model (e.g. `llava`, `moondream`) as the browser-side proxy. Requires **Vision Support** enabled on that model. Stored in `WasmKeyStore` / `OllamaModelSettings.IsVisionProxy`. |
| **Server proxied providers** (`appsettings` + `AiProviderProxyOptions`) | `IsVisionProxy` on a model entry | Same pattern for CORS-restricted cloud models routed via `POST /api/proxy/chat`. Handled in `AiProviderProxyService.ApplyVisionProxyAsync`. |
| **Chat UI** (`Chat.razor`) | Eye icon on input bar | Shows when the current model relies on a proxy; tooltip names the proxy model. |

### Code paths

| Path | Handler | When |
|------|---------|------|
| **Local Ollama** | `WasmChatCompletionService` → `DescribeAttachmentViaVisionProxyAsync` | `ollama/*` models without `SupportsVision`; proxy from `WasmKeyStore.GetVisionProxyModelName()` |
| **Proxied cloud** | `AiProviderProxyService.ApplyVisionProxyAsync` | Models via `/api/proxy/chat` where target lacks vision but provider has `VisionProxyModelId` |

### Design notes & future direction

- **Single proxy per scope** — only one Ollama model can be `IsVisionProxy` at a time (`WasmKeyStore` clears others on save). Same for server provider config.
- **Last user turn only** — proxy runs on the most recent user message with attachments (not the full history), keeping token cost bounded.
- **PDF support** — PDFs are treated like images through the same describe/summarize prompt path.
- **Expansion candidate** — this routing slot could grow into richer orchestration (e.g. route by attachment type, pick cheapest vision model, or a custom trained routing model) without changing the chat UI contract.

**Key files:** `LocalAi.razor`, `OllamaModelSettings.cs`, `WasmKeyStore.cs`, `WasmChatCompletionService.cs`, `AiProviderProxyService.cs`, `AiProviderProxyOptions.cs`, `Chat.razor`

---

## Tool Use

| Source | Where it runs | How WASM reaches it |
|--------|---------------|---------------------|
| **AppTools** (`search_web`, `summarize_url`, `get_time`) | Server | `POST /api/tools/*` (public, no login required) |
| **MCP servers** | Remote MCP HTTP endpoints | Browser calls MCP directly; tools discovered in `McpToolSource` |
| **DefaultToolProvider** | Combines AppTools + MCP | Registered in both host and Client DI |

Tool execution traces are shown in the chat UI (`ToolExecutionTrace`). Models that support function calling get an automatic multi-turn tool loop via `UseFunctionInvocation`.

---

## Cross-Device Sync (SignalR + WebRTC)

Sync requires **email login** on both devices. The server **never** stores or relays chat/note payloads—only auth, presence, and small WebRTC signaling messages.

### Phase 1 — Presence (SignalR)
1. Authenticated WASM client connects to `/sync-hub` (`SyncHub`, `[Authorize]`).
2. Client calls `RegisterDevice(deviceId, deviceName)`; server tracks connections in `DevicePresenceService` (in-memory).
3. Hub broadcasts `DevicesUpdated` to the user's group `user:{userId}`.
4. **Sync.razor** shows online devices, rename, AI-server selection, auto-sync toggles.

### Phase 2 — Data sync (WebRTC DataChannel)
1. Initiator (`WasmSyncService`) opens a WebRTC peer connection; **offer/answer/ICE** exchanged via SignalR hub methods (`WebRtcSignaling`).
2. JS helpers in `Components/App.razor` (`webrtcCreatePeerConnection`, `webrtcSendData`, etc.) manage `RTCPeerConnection` + `RTCDataChannel`.
3. **Manifest exchange** first: both sides send fingerprints of conversations/notes (`SyncFingerprint`); only changed items are transferred.
4. Encrypted content never touches the server—payloads are JSON over the DataChannel (`sync-data`, `note-sync-data`, chunked for large blobs).
5. Receiver decrypts with the shared per-user key and writes to IndexedDB; UI refreshes via `OnConversationsChanged` / `OnNotesChanged`.

### AI relay (WebRTC)
A phone/tablet without Ollama can designate another online device as **AI server**. Chat completions for that client are sent over a dedicated DataChannel (`chatfish-ai-proxy`) to the peer running local models (`WasmChatCompletionService` on the server device).

### Architecture diagram

![Cross-device sync: SignalR for signaling, WebRTC for encrypted data](/images/SyncArchitecture.png)

**Signaling path:** Browser A ↔ SignalR `/sync-hub` ↔ Browser B  
**Data path:** Browser A ↔ WebRTC DataChannel ↔ Browser B (encrypted JSON)  
**Server sees:** cookies, device IDs, SDP/ICE blobs—not chat content.

---

## Server Database (SQLite)

| Table / entity | Purpose |
|----------------|---------|
| `Users` | Email, magic-link token, `LocalEncryptionKey` (protected) |
| `UserProviderKeys` | Optional server-stored provider API keys (importable to WASM) |
| `DataProtectionKeys` | ASP.NET key ring for encrypting secrets at rest |

**Not stored:** WASM conversation history, note bodies, or sync payloads.

---

## Key Files Reference
 
 ### Host — startup & shell
 
 | File | Description |
 |------|-------------|
 | `Program.cs` | App builder: Blazor modes, SQLite, cookie auth, SignalR hub, forwarded headers, magic-link routes |
 | `Components/App.razor` | HTML shell for WASM; hosts global JS (IDB, crypto, WebRTC) |
 | `Components/Routes.razor` | Router + `AdditionalAssemblies` to find shared components |
 
 ### Authentication & APIs
 
 | File | Description |
 |------|-------------|
 | `Apis/WasmApiEndpoints.cs` | `/api/auth/*`, `/api/user/encryption-key`, `/api/keys`, `/api/tools/*` |
 | `Apis/AiProxyEndpoints.cs` | `/api/proxy/providers`, `/api/proxy/chat` for CORS-restricted models |
 | `Services/MagicLinkService.cs` | Create/validate magic-link tokens |
 | `Data/ChatfishDbContext.cs` | EF Core context for server DB |
 
 ### Sync & presence
 
 | File | Description |
 |------|-------------|
 | `Apis/SyncHub.cs` | SignalR hub: device registration, WebRTC signaling relay |
 | `Services/DevicePresenceService.cs` | In-memory online device registry per user |
 
 ### Shared UI (`ChatfishApp.Shared`)
 
 | File | Route (approx) | Description |
 |------|---------------|-------------|
 | `Components/LoginPage.razor` | `/` | Landing, magic-link login, guest continue |
 | `Components/ChatPage.razor` | `/chat` | Main chat UI, sidebar, attachments, tool traces |
 | `Components/NotesPage.razor` | `/notes` | Notebooks, Quill entries, floating add button |
 | `Components/SyncPresencePage.razor` | `/sync` | Device list, sync targets, auto-sync, AI server pick |
 | `Components/LocalAiPage.razor` | `/local-ai` | Ollama URL, model discovery |
 | `Components/CloudProvidersPage.razor` | `/cloud-providers` | API keys for Groq, OpenRouter, Gemini, etc. |
 | `Components/SettingsPage.razor` | `/settings` | Profile, system prompt, preferences |
 | `Components/ToolsPage.razor` | `/tools` | Enable MCP servers and tokens |
 | `Layout/AppLayout.razor` | - | Main cohesive layout for both WASM & MAUI |
 
 ### Shared Logic (`ChatfishApp.Shared`)
 
 | File | Description |
 |------|-------------|
 | `Services/ChatCompletionService.cs` | Core completion loop + tool execution logic |
 | `Services/ChatModelCatalogService.cs` | Manage available AI models across providers |
 | `Services/Mcp/McpToolSource.cs` | Discover and cache MCP tools from enabled servers |
 | `Services/Tools/AppTools.cs` | Core built-in tool implementations (`search_web`, etc.) |
 
 ### Business Contracts (`ChatfishApp.Core`)
 
 | File | Description |
 |------|-------------|
 | `Storage/IConversationStore.cs` | Interface for chat history persistence |
 | `Storage/INoteStore.cs` | Interface for notes persistence |
 | `Storage/ICryptoService.cs` | Interface for AES-GCM encryption/decryption |
 | `Sync/ISyncService.cs` | Interface for cross-device synchronization |
 
 ### Client Implementations (WASM vs MAUI)
 
 | Feature | WASM Implementation (`ChatfishApp.Client`) | MAUI Implementation (`ChatfishApp.Maui`) |
 |---------|-------------------------------------------|------------------------------------------|
 | **Conversations** | `Services/WasmConversationStore.cs` (IndexedDB) | `Services/SqliteConversationStore.cs` (SQLite) |
 | **Notes** | `Services/WasmNoteStore.cs` (IndexedDB) | `Services/SqliteNoteStore.cs` (SQLite) |
 | **Encryption** | `Services/WasmCryptoService.cs` (WebCrypto JS) | `Services/MauiCryptoService.cs` (Native .NET) |
 | **Sync** | `Services/WasmSyncService.cs` | `Services/MauiSyncService.cs` |
 | **Keys/Settings** | `Services/WasmKeyStore.cs` (localStorage) | `Services/SqliteKeyStore.cs` (SQLite) |


---

## Typical Agent Onboarding
 
 1. Read this doc and skim `wwwroot/ROADMAP.md` for direction (not current state).
 2. For **chat/AI** changes → `ChatPage.razor`, `ChatCompletionService` (Shared), `ChatModelCatalogService`.
 3. For **storage/privacy** → `IConversationStore`/`INoteStore` (Core) and the respective implementations in `ChatfishApp.Client` or `ChatfishApp.Maui`.
 4. For **vision proxy / model routing** → `LocalAiPage.razor`, `ChatCompletionService`, `WasmKeyStore`/`SqliteKeyStore`.
 5. For **sync** → `ISyncService` (Core), `SyncPresencePage.razor`, and platform implementations of `ISyncService`.
 6. For **new API endpoints** → `WasmApiEndpoints.cs` or `AiProxyEndpoints.cs`; register in host `Program.cs`.
 7. For **tools/MCP** → `AppTools.cs`, `McpToolSource` (Shared), `ToolsPage.razor`.


---

*Last updated: June 2026 — reflects the WASM-first local-storage architecture.*