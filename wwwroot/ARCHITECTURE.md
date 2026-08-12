# Wizionic Architecture

**Purpose:** Quick reference for humans and AI agents working on this codebase. Describes what exists today (not the future roadmap). For planned work see [ROADMAP.md](/roadmap).

**Stack:** .NET 10 · Blazor Web App (Auto: server shell + Interactive WebAssembly) · Blazor Hybrid (MAUI Windows/mobile) · Linux desktop (GirCore Adwaita + WebKit) · SQLite · SignalR · WebRTC · Microsoft.Extensions.AI · WebView2 (Windows browser) / WebKitGTK (Linux browser)

---

## Core Values

- **Privacy-first** — Chat history, notes, gallery images, and calendar events live on the client (IndexedDB / SQLite), encrypted at rest. The server does not store conversation or personal content for WASM/MAUI paths.
- **Local AI** — **Ollama** and **AMD Lemonade Server** on the user's machine are first-class providers (chat, multimodal, and Lemonade-specific modalities). A logged-in device can relay chat AI to other devices over WebRTC.
- **Login is optional** — Guests can chat and take notes immediately. Email + magic link is only needed for cross-device sync and encrypted key distribution.
- **Minimal server footprint** — Server handles auth, signaling, tool proxies (CORS), CORS-restricted AI proxies, OAuth broker for connectors, and optional Home Server install. Heavy lifting runs on the client.
- **Tool-rich agents** — Native app tools (Notes, Gallery, Calendar, web search, weather), optional OAuth OpenAPI connectors, user-selected MCP servers, plus MAUI Home Assistant and embedded browser — all via `Microsoft.Extensions.AI` function calling and a rules / AI / hybrid tool router.
- **Local-first sync** — WebRTC DataChannel carries encrypted chats, notes, gallery, calendar, and selected settings; SignalR is presence + signaling only.
- **Low-cost cloud** — Favor free or inexpensive models (proxied providers in `appsettings`, user API keys in browser storage).

---

## Solution Layout
 
 ```
 App/
 ├── App.csproj          # Host (Server): ASP.NET Core, APIs, SignalR hub, SQLite, auth
 ├── App.Core/           # Business Logic & Contracts: Interfaces, DTOs, shared models
 ├── App.Shared/         # Shared UI & logic: Razor components, Layouts, Common services (used by both WASM & MAUI)
 ├── App.Client/         # WASM Implementation: Browser-specific implementations (IndexedDB, JS Crypto)
 ├── App.Maui/           # Desktop/mobile clients: MAUI (Win/iOS/Android) + Linux desktop (net10.0)
 ├── Components/                 # Server shell for Blazor Web App (App.razor, Routes.razor)
 ├── Apis/                       # Host API endpoints (WasmApiEndpoints, SyncHub, etc.)
 ├── Data/                       # Server-side EF Core entities + AppDbContext
 ├── Services/                   # Server-only services: email, key protection, AI proxy
 ├── Pages/                      # Server-rendered pages (Roadmap, Architecture)
 └── wwwroot/                    # Static assets and documentation
 ```
 
 ### Project Sharing Model: WASM vs desktop clients
 
 | Layer | Shared? | Role |
 |-------|---------|------|
 | **`App.Core`** | ✅ Yes | Defines the "what": Interfaces (`IConversationStore`, `ISyncService`) and DTOs. No platform-specific code. |
 | **`App.Shared`** | ✅ Yes | Defines the "how it looks": Razor components (`ChatPage`, `NotesPage`), Layouts, and logic common to WASM & desktop. |
 | **`App.Client`** | ❌ No | WASM-specific: Implements Core interfaces using browser APIs (IndexedDB, WebCrypto). |
 | **`App.Maui`** | ❌ No | Native desktop/mobile: SQLite storage, SIPSorcery WebRTC, platform browser hosts. **Windows/mobile** = MAUI Blazor Hybrid + WebView2; **Linux** = GirCore Adwaita + WebKit (not full MAUI). |


---

## Authentication & Encryption

### Guest mode
- No cookie. `WasmAuthService` generates a per-browser **guest encryption key** in IndexedDB (`guest-encryption-key`).
- Data namespace: `wasmchat-` (conversations and notes).

### Logged-in mode
- User requests magic link → email via Brevo → `/magic-login?token=...` sets a **persistent** `AppAuth` cookie (survives browser restarts; renewed on activity via sliding expiration; cleared only on explicit sign-out).
- WASM calls `/api/auth/me` and `/api/user/encryption-key` (cookie sent automatically, same origin).
- Per-user **server encryption key** (random, protected at rest in SQLite via ASP.NET Data Protection).
- Data namespace: `u-{userId}-`.
- On login, `WasmGuestDataMigrationService` re-encrypts guest IndexedDB data into the authenticated namespace.

### At-rest encryption
- Conversation, note, gallery image, and calendar event **content blobs** are AES-256-GCM encrypted before local write (`WasmCryptoService` + JS helpers on WASM; native crypto on MAUI).
- Metadata (titles, dates, album/calendar names, sync flags) is cleartext for fast listing.
- User OAuth access tokens and MCP tokens live in `IKeyStore` (encrypted when signed in), not in the central server DB.

---

## Chat Flow

```
User types in ChatPage.razor
        │
        ▼
ChatCompletionService.CompleteAsync()  (Shared)
        │
        ├── Build history from IConversationStore (decrypted messages)
        ├── Prepend system prompt (profile settings from IKeyStore)
        ├── Trim history to context window (reserve room for reply); track stats
        ├── If model supports tools → CompositeRequestRouter (Rules / AI / Hybrid)
        │       ├── Native tools (search_web, summarize_url, get_time, …) via /api/tools/*
        │       ├── Notes / Gallery / Calendar tool modules (local stores)
        │       ├── OAuth OpenAPI connector tools (when installed)
        │       ├── MCP tools from McpToolSource
        │       └── Lemonade client tools (image/STT/TTS) when configured
        └── ChatModelCatalogService.GetChatClientForModel()
                ├── ollama/*     → direct to Ollama OpenAI-compat (default :11434/v1)
                ├── lemonade/*   → direct to Lemonade OpenAI-compat (default :13305/v1)
                │                 · image models → LemonadeImageService (not chat loop)
                │                 · Omni collections → server-side multimodal tools
                ├── proxied/*    → POST /api/proxy/chat (server-side key)
                └── user keys    → direct to Groq, OpenRouter, Gemini, etc.
        │
        ▼
Streaming tokens → UI (TTFT / total / ctx used/limit); IConversationStore saves encrypted JSON
        │
        └── If authenticated + auto-sync on → ISyncService queues WebRTC sync
```

**Context compact:** Toolbar button summarizes older turns to free window space without clearing the chat.

### Notes (local notebooks + AI tools)

| Piece | Detail |
|-------|--------|
| **UI** | `NotesPage.razor` (`/notes`) — notebooks, Quill HTML entries, floating add |
| **Store** | `INoteStore` — WASM IndexedDB / MAUI SQLite; bodies AES-GCM encrypted; titles cleartext |
| **Chat handoff** | “Add to notes” from chat; optional images via `IConversationMediaBuffer` |
| **AI tools** | `NotesToolModule`: `list_notebooks`, `list_note_entries`, `create_notebook`, `add_note_entry`, `append_to_note_entry` |
| **Routing** | Attached when `ContextualRequestRouter.MessageSuggestsNotesTools` (or AI router picks `Notes`) |
| **Protection** | Password-protected notebooks blocked for tools until unlocked in the UI |
| **Sync** | Auto-sync after local save via `INotesSyncBridge`; merge via `NoteSyncMerger` (entry LWW) |

### Gallery (albums + AI tools)

| Piece | Detail |
|-------|--------|
| **UI** | `GalleryPage.razor` (`/gallery`) — albums, grid, lightbox, password-protect album, reorder |
| **Store** | `IGalleryStore` — album meta + encrypted image bytes; thumbs for grid; short-lived display URLs for lightbox (avoid multi-MB data URLs in Blazor) |
| **Chat handoff** | Lemonade/chat-generated images can be saved into albums; `IConversationMediaBuffer` + `IGalleryChatHandoff` |
| **AI tools** | `GalleryToolModule`: `list_gallery_albums`, `list_recent_chat_images`, `save_to_gallery` |
| **Routing** | Gallery intent heuristics, or image generate + “save to gallery” paths; often co-attached with Lemonade |
| **Sync** | `SyncItemKind.Album` (meta only) + `AlbumImage` (per-image); `GallerySyncMerger` / bridges |
| **Quota** | `IStorageQuotaService` + `SumStoredImageBytesAsync` (meta size sum) |

No server-side gallery storage.

### Calendar (local multi-calendar + AI tools)

| Piece | Detail |
|-------|--------|
| **UI** | `CalendarPage.razor` (`/calendar`) — Google Calendar–style Day / Week / Month / Year; mini-month sidebar; color + visibility layers |
| **Models** | RFC 5545–aligned `LocalCalendar` / `CalendarEvent`; `WorkflowId` (`X-WIZIONIC-WORKFLOW`) reserved for future workflow triggers |
| **Store** | `ICalendarStore` — meta cleartext for grid; full event JSON AES-GCM encrypted |
| **iCalendar** | `CalendarIcs` (Ical.Net) — export/import `.ics`, RRULE presets, occurrence expansion for visible ranges |
| **AI tools** | `CalendarToolModule`: `list_calendars`, `list_events`, `add_calendar_event`, `update_calendar_event`, `delete_calendar_event` |
| **Routing** | `MessageSuggestsCalendarTools` or AI router module `Calendar` |
| **Sync** | `SyncItemKind.Calendar` / `CalendarEvent`; `CalendarMetaSyncPayload` / `CalendarEventSyncPayload` |

No server-side calendar storage.

---

## Local AI: Ollama + AMD Lemonade Server

Wizionic treats **two local OpenAI-compatible servers** as first-class peers. Both are configured on **Local AI** (`LocalAiPage.razor`); settings stay on-device (`IKeyStore`).

| Backend | Default base URL | Model id prefix | Catalog / metadata |
|---------|------------------|-----------------|-------------------|
| **Ollama** | `http://localhost:11434` | `ollama/{name}` | `/api/tags` + `/api/show`; falls back to `/v1/models` when the URL is Lemonade-compatible |
| **Lemonade** | `http://localhost:8000` or `http://localhost:13305` (AMD installer) | `lemonade/{id}` | `/api/v1/models` or `/v1/models` (`max_context_window`, labels) |

### Dual config UI

- Separate base URL (and optional Lemonade API key) fields; **Refresh models** per section.
- Per-model: label, context size, vision, tools (Ollama); Lemonade models also get modality flags from labels (`chat`, `image`, `edit`, `tts`, `transcription`, collections).
- Pointing the **Ollama** base URL at Lemonade is supported: list + context use OpenAI-compatible endpoints when Ollama `/api/tags` / `/api/show` are missing.
- Browser HTTPS → local HTTP is **mixed content**; prefer MAUI, or set CORS origins (`OLLAMA_ORIGINS`, `LEMONADE_ALLOWED_ORIGINS`) and avoid mixed content on the public site.

### Model routing in the chat picker

| Selection | Behavior |
|-----------|----------|
| `ollama/*` chat model | Streaming chat completion to Ollama |
| `lemonade/*` chat model | Streaming chat completion to Lemonade |
| `lemonade/*` **image** model (`IsImageGeneration`) | Send = image generate/edit (not the chat loop); param strip (steps, CFG, size, seed, upscale) |
| `lemonade/*` **Omni** collection (`IsOmniCollection`) | Chat completion with **client tools disabled** so Lemonade’s server-side planner owns image/edit/TTS |

Capability icons and toolbar actions (palette, mic, TTS) follow the **selected model** and whether Lemonade modalities are available.

**Key files:** `LocalAiPage.razor`, `LemonadeModelSettings.cs` / `OllamaModelSettings` (Core), `LemonadeModelCatalogResolver.cs`, `OllamaCapabilitiesResolver.cs`, `WasmKeyStore` / `SqliteKeyStore`, `ChatModelCatalogService.cs`, `ChatModelInfo.cs`

---

## Lemonade modalities (image, speech, Omni)

Beyond chat, Lemonade exposes modality-specific APIs used from `ChatPage` and optional client tools.

### Image generation & edit

| Capability | Service | API (typical) | UI |
|------------|---------|---------------|-----|
| **Generate** | `ILemonadeImageService` / `LemonadeImageService` | Lemonade images endpoint (OpenAI-style or Lemonade extensions) | Palette button / + menu when a Lemonade model is selected; advanced panel (model, steps, CFG, WxH, seed, upscale) |
| **Edit (img2img)** | Same; only models with **`edit`** label | Edit path with source image bytes | + menu / bot ⋮ “Edit image”; edit fails on generate-only models (e.g. some Z-Image) |
| **Upscale** | Optional post-step (e.g. RealESRGAN variants) | After generate/edit | Panel upscale selector |

Results are stored as **attachments** on assistant messages (PNG base64). Copy / download via JS interop (`chatInterop.js` / `App.razor`).

### Speech-to-text (STT)

- `ILemonadeSpeechService` — record mic (JS WAV interop) → Lemonade **transcription** model (e.g. Whisper).
- Mic button on the input toolbar when STT is available (not on pure image models).
- Transcript fills the chat textarea for send.

### Text-to-speech (TTS)

- Same speech service → Lemonade **TTS** model (e.g. Kokoro).
- Toolbar: auto-speak replies toggle + “read last reply”; per-message **Speak** / bot or user ⋮ **Read aloud**.
- Playback via JS (`appPlayAudioBase64`); Omni may already attach audio — prefer Omni audio over client TTS when present.

### Omni collections

- Models labeled as **collections / Omni** plan multimodal steps **on the Lemonade server** (image, edit, speech).
- Wizionic runs a normal streaming completion but **disables client function-invocation** for that turn so client tools do not compete with the collection planner.
- Embedded data-URI images/audio in the reply are extracted by `OmniMediaExtractor` into `Attachment`s for the chat UI.

### Lemonade client tools (`LemonadeToolModule`)

When Lemonade is configured, optional ME.AI tools can expose generate/edit/STT/TTS to **tool-capable chat models** (subject to PureChat routing). Omni turns keep client tools off.

**Key files:** `LemonadeImageService.cs`, `LemonadeSpeechService.cs`, `OmniMediaExtractor.cs`, `LemonadeToolModule.cs`, `ChatPage.razor`, `chatInterop.js`, `App.razor` (mic + audio helpers)

---

## Streaming, generation stats, and context management

### Streaming & stop

- Local Ollama and Lemonade chat use `GetStreamingResponseAsync` with progressive UI updates (`onPartialText`).
- **Stop** cancels the active `CancellationTokenSource` mid-stream.
- `MaxOutputTokens` (e.g. 4096) caps runaway generations.

### Stats line (per assistant message)

Collapsible “Model reasoning / tool calls / stats” includes a compact line from `ChatCompletionStats.FormatLine()`:

- **TTFT** — time to first token after the model HTTP call starts  
- **total** — wall time for the model call  
- **prep** — client setup before the request (if ≥ ~50 ms)  
- **in / out** tokens, **tok/s**, **stream** / **stopped**  
- **ctx used/limit (pct%)** — context window fill (server usage or estimate)  
- **trimmed N** — older history messages dropped for this request  

### Tool routing modes (Rules / AI / Hybrid)

Dumping every tool schema into a small local model inflates prefill and can degrade answer quality. Wizionic attaches **only the modules needed for the turn**.

| Mode (`ToolRoutingMode` in `IKeyStore`) | Behavior |
|----------------------------------------|----------|
| **Rules** (default) | `ContextualRequestRouter` heuristics only — wake word / HA session stickiness, browser panel, Notes/Gallery/Calendar/image/utility keywords. Zero router model cost. |
| **AI** | `AiRequestRouter` asks a configured **routing model** (`ToolRoutingModelId`, e.g. a small Lemonade/Ollama chat model) to return JSON module names. No tools on the router call. Falls back to rules on timeout / parse failure. HA session stickiness is **off** so weather after lights still reclassifies. |
| **Hybrid** | Rules first; if the route is “strong” (HA wake word, browser panel, clear Lemonade image intent) keep it. Otherwise call the AI router (covers PureChat, Gallery-only, multi-intent). Without a routing model configured, Hybrid/AI degrade to Rules. |

`CompositeRequestRouter` is the app-wide `IRequestRouter`. `ChatCompletionService` records the route in tool traces (`🧭 Route: …`) and appends module-specific system instructions when needed.

Known module names for the AI router: `Native`, `Lemonade`, `Gallery`, `Calendar`, `Notes`, `HomeAssistant`, `BrowserAgent` (plus MCP tools when utility/MCP path is open).

**Configure:** Settings / chat tool-routing UI → mode + catalog model id. Stored in `WasmKeyStore` / `SqliteKeyStore` and can sync under **Tools** / related settings categories.

### Context window UI & compact

| Mechanism | Behavior |
|-----------|----------|
| **Auto-trim** | Before each completion, drop oldest non-system turns so estimated tokens fit under `contextLimit − reserveOutput` |
| **Context button** | Toolbar pill `used/limit` (e.g. `1.2k/32.8k`); **orange ≥80%**, **red ≥90%**; click runs **compact** |
| **Compact** | Isolated summarize call over older turns → one summary assistant message; keeps last ~6 messages; frees window without full clear |

**Key files:** `IChatCompletionService.cs` / `ChatCompletionStats`, `ChatCompletionService.cs` (`TrimHistoryToContext`, streaming), `ChatPage.razor` (ctx button, compact), `app.css` (`.ctx-btn`)

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

Tools are composed by `CompositeToolProvider` from injectable **`IToolModule`** implementations plus cached MCP / OpenAPI connector tools. Each module exposes `ModuleName`, `IsAvailable`, and a list of `AITool` functions via `Microsoft.Extensions.AI`.

| Module | Tools | Where it runs | Availability |
|--------|-------|---------------|--------------|
| **Native** (`NativeToolModule`) | `search_web`, `summarize_url`, `get_time`, `calculate`, `get_current_weather` | Host via `POST /api/tools/*` | When utility intent / AI router attaches `Native` |
| **Notes** (`NotesToolModule`) | `list_notebooks`, `list_note_entries`, `create_notebook`, `add_note_entry`, `append_to_note_entry` | Client → `INoteStore` | Always registered; attached on notes intent |
| **Gallery** (`GalleryToolModule`) | `list_gallery_albums`, `list_recent_chat_images`, `save_to_gallery` | Client → `IGalleryStore` | Always registered; attached on gallery/image-save intent |
| **Calendar** (`CalendarToolModule`) | `list_calendars`, `list_events`, `add_calendar_event`, `update_calendar_event`, `delete_calendar_event` | Client → `ICalendarStore` | Always registered; attached on calendar intent |
| **Lemonade** (`LemonadeToolModule`) | Image generate/edit, STT, TTS helpers | Client → Lemonade base URL | When Lemonade models/services are configured; off for Omni turns |
| **OpenAPI OAuth connectors** (`OpenApiConnectorToolSource`) | Curated tools per installed connector (Gmail, GitHub, …) | Client → host `/api/connectors/*` proxy with user tokens | When user has connected OAuth connectors |
| **MCP servers** (`McpToolSource`) | User-enabled remote MCP tools | Client → MCP HTTP/SSE remote URL | When servers installed + enabled (+ token if required) |
| **HomeAssistant** (`HomeAssistantToolModule`) | `ListEntities`, `ListLights`, `ControlLight`, … | MAUI → direct LAN HTTP to HA | MAUI only, when HA configured |
| **BrowserAgent** (`BrowserAgentToolModule`) | `navigate_to`, `get_page_content`, `click_element`, `fill_field` | MAUI → native WebView JS eval | MAUI only, when browser panel open |

**Routing:** Before each completion, `CompositeRequestRouter` (Rules / AI / Hybrid — see [Tool routing modes](#tool-routing-modes-rules--ai--hybrid)) classifies the last user message and narrows the tool set. HA and Browser still force module tools when their panel/wake-word routes apply.

On WASM, `HomeAssistantToolModule` and `BrowserAgentToolModule` are not registered; null services satisfy Core interfaces but expose no agentic tools.

Tool execution traces are shown in the chat UI (`ToolExecutionTrace`). Models that support function calling get an automatic multi-turn tool loop via `UseFunctionInvocation`.

---

## Tools page, MCP registry & OAuth connectors

**UI:** `ToolsPage.razor` (`/tools`) — single **Tools** experience:

1. **Installed** — OAuth connectors with tokens + enabled MCP (and custom MCP URLs).
2. **Discover** — two-column cards: uninstalled OAuth catalog rows + remote-capable MCP from the official registry. Search is **server-side** (not a client filter of 20 rows). Card body opens details; only **Install** / **Connect** performs the action.

### Official MCP registry (discovery only)

| Piece | Detail |
|-------|--------|
| Upstream | `https://registry.modelcontextprotocol.io` (`/v0.1/servers`, fallback `/v0`) |
| Host proxy | `GET /api/tools/mcp-registry?q=&limit=` in `WasmApiEndpoints` (CORS-safe for WASM) |
| Default browse | ~20 **remote-capable** servers (`streamable-http` / `sse` / `http` only — no stdio packages for browser/WASM) |
| Search | Upstream `search=` across the full registry; still filtered to remotes |
| Card fields | Title, description, publisher (namespace / GitHub), icon URL (registry `icons[]`, GitHub avatar, or favicon) |
| Persistence | **None** for browse/search. On install: enable flag + optional token + **URL snapshot** in KeyStore (custom-connector path) so tools work offline from the top-20 list |

### OAuth OpenAPI connectors (host broker)

| Piece | Detail |
|-------|--------|
| **Catalog** | SQLite `Connectors` table → `GET /api/connectors/catalog` (DB-only; empty table ⇒ no OAuth tiles) |
| **App credentials** | SQLite `OAuthProviders` (`ClientId` / protected secret) or env fallbacks |
| **Flow** | Host OAuth broker (`OAuthEndpoints`) + PKCE/session handoff; user tokens land in **client KeyStore** only |
| **MAUI** | In-app browser / URI launcher + `MauiOAuthInterceptor`; `IAppNavigation` returns to Tools after connect |
| **Proxy** | `ConnectorProxyEndpoints` + `OpenApiConnectorToolSource` — curated OpenAPI tools executed with the stored access token |
| **Sync** | Installed connector config + tokens travel in settings category **Tools** (encrypted local storage → WebRTC) |

**Key files:** `ToolsPage.razor`, `WasmApiEndpoints` (mcp-registry), `Apis/OAuthEndpoints.cs`, `ConnectorCatalogEndpoints.cs`, `ConnectorProxyEndpoints.cs`, `OpenApiConnectorToolSource`, `McpToolSource`, `Data/Connector.cs`, `Data/OAuthProvider.cs`

---

## Home Assistant (MAUI)

Wizionic can agentically control a local Home Assistant instance from the MAUI desktop app. Configuration lives at `/home-assistant` (`HomeAssistantPage.razor`); the chat window drives devices once a long-lived access token and base URL are saved.

### Configuration & storage

| Setting | Stored in | Purpose |
|---------|-----------|---------|
| Base URL | `IKeyStore.HomeAssistantBaseUrl` | e.g. `http://192.168.4.23:8123` |
| Long-lived token | `IKeyStore.HomeAssistantToken` | Bearer token from HA Profile → Security |
| Assistant name (wake word) | `IKeyStore.HomeAssistantAssistantName` | Default `Home` — user addresses this name in chat |
| Device summary cache | `IKeyStore.HomeAssistantDeviceSummary` | Cleartext multi-domain controllable entity catalog refreshed on save/test |

Credentials are normalized by `HomeAssistantCredentials` (Core) and persisted in SQLite via `SqliteKeyStore` (`HomeAssistantConfig` DTO). The settings page calls `ISmartHomeService.TestConnectionAsync` and `BuildDeviceCatalogAsync` to validate and refresh the device list.

### Core contracts

| Interface / type | Location | Role |
|------------------|----------|------|
| `ISmartHomeService` | `App.Core/SmartHome/` | `TestConnectionAsync`, `CallServiceAsync`, `GetEntityStateAsync`, `ListEntitiesAsync`, `BuildDeviceCatalogAsync`, `ListServicesAsync`, `ProcessConversationAsync`, `ListLightEntitiesAsync` |
| `HomeAssistantCredentials` | `App.Core/SmartHome/` | URL/token normalization |
| `HomeAssistantConfig` | `App.Core/Storage/` | DTO for key store persistence |

### MAUI implementation

`HomeAssistantService` (MAUI) is a direct LAN `HttpClient` — calls never go through the Wizionic server or browser DevTools. It hits standard HA REST endpoints with `Authorization: Bearer {token}`:

| Endpoint | Use |
|----------|-----|
| `GET /api/` | Connection test |
| `GET /api/states` | Entity discovery + multi-domain catalog |
| `GET /api/states/{entity_id}` | Single entity state |
| `GET /api/services` | List services (optional filter by domain) |
| `POST /api/services/{domain}/{service}` | Control any device |
| `POST /api/conversation/process` | Secondary Assist natural-language path |

Proxy is disabled (`UseProxy = false`) to avoid LAN hangs.

**Control strategy:** Wizionic’s selected model (Ollama or cloud) is the agent. REST tools (`ListEntities` → `CallService` / `ControlLight` / `ControlMediaPlayer`) are the primary path so any controllable domain works for any user’s HA install.

**Hybrid enforcement when the model skips tools** (common with small VL models):

1. First completion with HA tools available  
2. Tool-required retry via the same function-invocation client  
3. **Structured REST fallback** (`HomeAssistantFallback`) — parse volume/media/light intents and call HA services directly using catalog name match + domain-specific session entities (`LastMediaPlayerEntity` / `LastLightEntity`). Fixes “first volume works, follow-up fails” without relying on the model.  
4. **Clean Assist fallback** — `POST /api/conversation/process` with natural language only (friendly names, no raw `entity_id`s, no wrong-domain last entity, strip fillers like “now” that confuse Assist)  
5. If all fail → honest failure message (never keep a hallucinated “volume has been set”)

`ProcessConversation` remains available as a model-callable tool; the auto path does not depend on the model choosing it. Small vision models (e.g. 4B VL) are weaker at multi-arg tool calls — structured fallback + Assist + `ControlMediaPlayer` mitigate that; stronger instruct models remain best for reliability.

### How chat triggers Home Assistant

```
User message in ChatPage
        │
        ▼
ContextualRequestRouter.ClassifyRequest()
        ├── HomeAssistant module available (IsConfigured)?
        ├── Wake word present?  e.g. "Home, turn off kitchen light"
        │       OR active HA session in this conversation (15 min TTL)?
        └── Yes → route TargetModule = "HomeAssistant"
        │
        ▼
ChatCompletionService
        ├── Tool set = HomeAssistant tools + Native tools only
        ├── System prompt: BuildHomeAssistantPrompt() (device summary + session context)
        ├── Model calls tools via UseFunctionInvocation
        └── On success → IRoutingSessionStore records invocation (enables follow-ups)
```

**Wake word:** `ContextualRequestRouter.ContainsWakeWord` matches the configured assistant name as a whole word (regex word boundary). Multi-word names use substring match.

**Follow-ups:** After a successful HA tool call, follow-up messages like *"make it blue"* work for **15 minutes** in the same conversation without repeating the wake word (`InMemoryRoutingSessionStore` + `RoutingSession.SessionTtl`).

**Enforcement:** If the model replies without calling HA tools but claims it changed a device, `ChatCompletionService` retries with a tool-required prompt and may replace the response with an honest failure message.

### Agent tools (function names exposed to the model)

| Tool | Purpose | Example user intent |
|------|---------|---------------------|
| `ListEntities` | Discover entities by domain and/or search (primary discovery) | "Home, what media players do you see?" / find Denon by name |
| `ListLights` | Alias for light listing | "Home, what lights do you know?" |
| `ControlLight` | Turn on/off, brightness (0–255), color name or hex | "Home, turn off the kitchen light" / "make it blue" |
| `ControlMediaPlayer` | play/pause/stop/on/off/volume (0–100%)/select_source | "Home, set volume to 50" / play on AVR |
| `GetEntityState` | Read any entity state JSON | "Home, is the garage door open?" |
| `CallService` | Generic `domain.service` with JSON `service_data` (primary control) | Switches, climate, covers, scenes, advanced media |
| `ListServices` | List HA services for a domain | When unsure of service names for an integration |
| `ProcessConversation` | HA Assist NLU (`/api/conversation/process`) — secondary + auto-fallback | Area phrases; app also calls Assist if model skips tools |

**Key files:** `HomeAssistantPage.razor`, `HomeAssistantService.cs`, `HomeAssistantToolModule.cs`, `ContextualRequestRouter.cs`, `ChatCompletionService.cs` (`BuildHomeAssistantPrompt`, `RecordHomeAssistantSessionIfNeeded`)

---

## Embedded Browser (desktop)

The chat page can show a split view: chat on the left, embedded browser on the right. Toggle via the globe icon in `AppTopBar.razor` (`IBrowserPanelState`). When open, the model can navigate, read page text, click elements, and fill form fields agentically. Available on **Windows MAUI** and **Linux desktop**; WASM uses null browser services.

### WebView architecture (hybrid shell + native overlay)

Wizionic uses a **two-layer** pattern on every desktop host:

1. **Blazor shell** — renders all Razor UI (chat, toolbar, browser chrome).
2. **Native WebView overlays** — two platform WebViews sit on top of placeholder `<div>` hosts in the Blazor DOM, positioned from JS bounds.

| Platform | Blazor host | Overlay WebViews | Engine |
|----------|-------------|------------------|--------|
| **Windows** | MAUI `BlazorWebView` in `MainPage.xaml` | MAUI `WebView` → **WebView2** | Chromium/Edge |
| **Linux** | `WebKit.BlazorWebView.GirCore` in Adwaita window | GirCore `WebKit.WebView` in `Gtk.Overlay` | **WebKitGTK 6** |

On **Windows**, `BrowserWebViewPlatformService` configures `CoreWebView2` for new-window behavior, download prompts, and clear-on-exit. On **Linux**, see [Linux Desktop](#linux-desktop-maui-project-net100). This is **not** an in-DOM `<iframe>` — native views are overlaid at pixel coordinates reported from JavaScript.

```
MainPage.xaml (AbsoluteLayout)
├── BlazorWebView          ← chat + EmbeddedBrowser.razor chrome (HTML/CSS)
├── browserWebView         ← native WebView2, main browsing area
└── browserSideWebView     ← native WebView2, side-panel apps / bookmarks web view

EmbeddedBrowser.razor
├── #browser-content-host      ← empty div; bounds sent to MAUI
└── #browser-side-content-host ← empty div for side panel web content

browserInterop.js (ResizeObserver)
        │ getBoundingClientRect()
        ▼
BrowserOverlayService.ReportMainBounds / ReportSideBounds
        │ AbsoluteLayout.SetLayoutBounds(native WebView)
        ▼
Native WebView visible at correct position under Blazor toolbar
```

`BrowserOverlayService` implements `IBrowserOverlaySync`: it shows/hides overlays and caches bounds when dialogs (bookmark modal, PWA install) cover the web content area.

### Core contracts

| Interface | Location | Role |
|-----------|----------|------|
| `IBrowserAgentService` | `App.Core/Browser/` | Navigation, history, `EvaluateScriptAsync`, page text/HTML |
| `IBrowserContext` | `App.Core/Browser/` | Agent tool bridge (`NavigateAsync`, `GetPageContentAsync`, `ClickElementAsync`, `FillFieldAsync`) |
| `IBrowserStore` | `App.Core/Browser/` | Bookmarks, history, settings (SQLite on MAUI) |
| `IBrowserSidebarStore` | `App.Core/Browser/` | Pinned apps / vertical toolbar entries |
| `IBrowserPanelState` | `App.Core/UI/` | Browser panel open/closed, chat column width |
| `IBrowserSidePanelState` | `App.Core/UI/` | Side panel content (bookmarks, settings, web app) |
| `IBrowserOverlaySync` | `App.Core/Browser/` | Native overlay bounds + visibility |
| `IBrowserSideAgentService` | `App.Core/Browser/` | Side-panel WebView navigation |
| `IPwaDetector` | `App.Core/Browser/` | PWA manifest detection for install/pin |

### Platform implementations

| Service | Windows MAUI | Linux desktop | Role |
|---------|--------------|---------------|------|
| Main browser agent | `MauiBrowserAgentService` | `LinuxBrowserAgentService` | Navigation, history, `EvaluateScriptAsync` |
| Side browser agent | `MauiSideBrowserService` | `LinuxSideBrowserService` | Side-panel WebView |
| Overlay positioning | `BrowserOverlayService` | `LinuxBrowserOverlayService` | Bounds + visibility for native views |
| Host / layout | `MainPage.xaml` AbsoluteLayout | `LinuxBrowserHost` (`Gtk.Overlay`) | Places Blazor + two WebViews |
| Platform hooks | `BrowserWebViewPlatformService` (WebView2) | (WebKit settings in host) | Engine-specific config |
| Context / tools | `MauiBrowserContext`, `BrowserAgentToolModule` | same (shared) | Agent tool bridge |
| Persistence | `SqliteBrowserStore` / `SqliteBrowserSidebarStore` | same | Bookmarks, history, pinned PWAs |
| PWA detect | `MauiPwaDetector` | same (uses agent JS eval) | Manifest discovery |

**Windows wiring** (`MainPage.xaml.cs`): `agent.AttachWebView(browserWebView)`, `overlay.Initialize(...)`, `platform.Attach(browserWebView)`.

**Linux wiring** (`Platforms/Linux/Program.cs` + `LinuxBrowserHost`): Adwaita window content is `LinuxBrowserHost.BuildRoot(blazorWebView)`; main/side `WebKit.WebView`s attach to `LinuxBrowserAgentService` / `LinuxSideBrowserService`.

### JS interop (`browserInterop.js`)

JS is used for **layout and drag UX**, not for loading web pages:

| JS function | Called from | Purpose |
|-------------|-------------|---------|
| `appBrowser.startBoundsObserver` | `EmbeddedBrowser.razor` | `ResizeObserver` → `[JSInvokable] OnBrowserMainOverlayBounds` / `OnBrowserSideOverlayBounds` |
| `appBrowser.reportBoundsNow` | Overlay refresh | Force bounds recalc after dialogs close |
| `appBrowser.startSplitterDrag` | Chat/browser split | Resize chat column |
| `appBrowser.startSidePanelSplitterDrag` | Side panel split | Resize bookmarks/web side column |
| `appBrowser.startBookmarkBarDrag` / `startSidebarDrag` / `startVtoolbarDrag` | Bookmark & PWA toolbar | Reorder via drag-drop |
| `appBrowser.getWrapperWidth` / `getPanelAnchor` | Layout helpers | Split width, context menu positioning |

Agentic page interaction uses native JS eval in C#: Windows `WebView.EvaluateJavaScriptAsync` (`MauiBrowserAgentService`); Linux `WebView.EvaluateJavascriptAsync` (`LinuxBrowserAgentService`).

### How chat triggers browser control

Unlike Home Assistant, **no wake word** is required. When `IBrowserPanelState.IsOpen` and `BrowserAgentToolModule.IsAvailable`:

1. `ContextualRequestRouter` routes to `TargetModule = "BrowserAgent"`.
2. `ChatCompletionService` appends `BuildBrowserPrompt()` with current URL and page title.
3. Tool set = BrowserAgent tools + Native tools.
4. Chat placeholder changes to *"Ask about this page, or say 'navigate to…'"*.

### Agent tools (what users can ask)

| Tool | What it does | Example user requests |
|------|--------------|----------------------|
| `navigate_to` | Open a URL in the main embedded browser | "Go to wikipedia.org", "navigate to github.com" |
| `get_page_content` | Return visible text of the current page (scripts/styles stripped) | "Summarize this page", "What's on this page?", "Extract the prices" |
| `click_element` | `document.querySelector(selector).click()` | "Click the Sign in button", "Press #submit" |
| `fill_field` | Set input value + dispatch input/change events | "Fill the search box with 'cats'", "Enter my email in #email" |

The model chooses CSS selectors; complex multi-step flows combine `get_page_content` → `click_element` / `fill_field` → `navigate_to`. Native tools (`search_web`, `summarize_url`) remain available alongside browser tools.

### Browser UI features (non-agentic)

`EmbeddedBrowser.razor` provides a full mini-browser chrome:

- Toolbar: back/forward/refresh, URL bar with history suggestions, bookmarks menu, external open, settings
- Bookmarks bar + folders (stored in `IBrowserStore`)
- Side panel: bookmark manager, browser settings (search engine, homepage, clear data on exit)
- **Vertical toolbar (PWA bar):** pinned sites and installed PWAs (`IBrowserSidebarStore`)

### PWA vertical toolbar

The right-hand `browser-vtoolbar` lists pinned apps from `SidebarStore`. `MauiPwaDetector` watches navigation and detects `<link rel="manifest">` via in-page JS + HTML parse + HTTP guesses. When a manifest is found, the **+** button offers **Install app** (PWA metadata: name, icons, `start_url`, `display`, theme colors) or **Pin page only**. PWAs open in the side panel or main browser per `OpenTarget` (configurable via context menu). Drag-reorder uses `appBrowser.startVtoolbarDrag`.

**URL resolution:** Manifest members (`start_url`, icon `src`) resolve via `PwaManifestHelper.ResolveUrl`. On Linux/.NET, root-relative paths like `"/"` must **not** be passed to `Uri.TryCreate(..., UriKind.Absolute)` alone — that yields `file:///` and the browser normalizer turns them into a Brave search. Resolution always uses the manifest/page base URL; non-http(s) start URLs are refused at pin/open time. Broken pins from earlier builds are healed on load when a valid icon origin exists (`HealPinnedApp`).

**Key files:** `EmbeddedBrowser.razor`, `ChatPage.razor`, `browserInterop.js`, `MainPage.xaml` (Windows), `LinuxBrowserHost.cs` (Linux), `MauiBrowserAgentService.cs` / `LinuxBrowserAgentService.cs`, `BrowserAgentToolModule.cs`, `MauiPwaDetector.cs`, `PwaManifestHelper.cs`, `ContextualRequestRouter.cs`

---

## Linux Desktop (`App.Maui` / `net10.0`)

Linux is a **first-class desktop target** in the same `App.Maui` project, but it does **not** use the MAUI window stack (`UseMaui` is off for `net10.0`). The shell is a native **GTK4 / libadwaita** app hosting Blazor and browser overlays via **GirCore** bindings and **WebKitGTK**.

### Why not full MAUI on Linux?

| Constraint | Consequence |
|------------|-------------|
| .NET MAUI workload does not ship a supported Linux desktop TFM the way Windows/iOS/Android do | Target framework is plain **`net10.0`** with `#define LINUX_DESKTOP` |
| `UseMaui` / SingleProject / `MainPage.xaml` are disabled for that TFM | No MAUI `Application` / `BlazorWebView` (Maui package) |
| Embedded browser still needs a real WebView | Host **WebKitGTK 6** directly; Blazor UI via `WebKit.BlazorWebView.GirCore` |

Shared UI (`App.Shared`), Core contracts, SQLite stores, SIPSorcery sync, Home Assistant, and BrowserAgent tools are the same as Windows; only the **window host** and **WebView implementation** differ.

### Target frameworks (host OS)

Defined in `App.Maui.csproj`:

| Host OS | Typical TFMs |
|---------|----------------|
| **Linux** | `net10.0` (always); `net10.0-android` if Android SDK present |
| **Windows** | `net10.0-windows10.0.19041.0` (+ mobile TFMs when configured) |
| **macOS** | `net10.0-ios`, `net10.0-maccatalyst` (when not on Linux) |

Linux-only sources: `Platforms/Linux/**`, `Services/Linux/**`. Windows/mobile platforms and `MainPage`/`App` XAML are **removed from compile** when `TargetFramework == net10.0`.

### NuGet packages (Linux TFM)

| Package | Version (as of writing) | Role |
|---------|-------------------------|------|
| **`WebKit.BlazorWebView.GirCore`** | `10.0.0-rc.1` | Blazor Hybrid host on WebKitGTK — `BlazorWebView`, DI registration, GLib dispatcher |
| **`GirCore.WebKit-6.0`** | `0.7.0-preview.2` | C# bindings for WebKitGTK 6 (`WebKit.WebView`, JS eval, load events) |
| **`GirCore.Adw-1`** | `0.7.0-preview.2` | libadwaita (`Adw.Application`, `ApplicationWindow`, `HeaderBar`, `ToolbarView`) |
| **`GirCore.Gtk-4.0`** | `0.7.0-preview.2` | GTK4 widgets (`Gtk.Overlay`, layout, window chrome) |
| **`Microsoft.AspNetCore.Components.WebView`** | `10.0.0` | Shared Blazor WebView abstractions (not the MAUI package) |
| **`Microsoft.Maui.Controls`** | `$(MauiVersion)` e.g. 10.0.80 | Still referenced for shared types / assets; **not** the window host |
| **`Microsoft.Data.Sqlite`**, SignalR client, **SIPSorcery**, **Velopack**, config/logging packages | various | Same as other Maui TFMs |

**Not used on Linux:** `Microsoft.AspNetCore.Components.WebView.Maui` (Windows/mobile only).

**Native system deps (distro packages):** GTK 4, libadwaita-1, WebKitGTK 6 (`libwebkitgtk-6.0`), and transitive GLib/GObject stack. GirCore is P/Invoke over those shared libraries — they must be installed on the machine.

### Process / UI tree

```
Platforms/Linux/Program.cs  (Main)
        │
        ├── Adw.Module / Gtk.Module / WebKit.Module.Initialize()
        ├── Adw.Application.New("com.wizionic.app")
        │       └── RunWithSynchronizationContext(args)   ← GLib main loop + SyncContext
        │
        └── OnActivate
                ├── Adw.ApplicationWindow (title "Wizionic", min/max/close)
                │     └── Adw.ToolbarView
                │           ├── Adw.HeaderBar  (decoration layout :minimize,maximize,close)
                │           └── LinuxBrowserHost.BuildRoot(BlazorWebView)
                │                 └── Gtk.Overlay
                │                       ├── BlazorWebView (WebKit)  ← Shared Routes / chat UI
                │                       ├── WebKit.WebView          ← main embedded browser
                │                       └── WebKit.WebView          ← side-panel browser
                ├── MauiProgram.CreateLinuxServiceProvider()
                └── LinuxDesktopIcon.Apply(window)  ← dock icon + .desktop entry
```

**Important:** `RunWithSynchronizationContext` is required so Blazor/`IDispatcher` and GirCore callbacks share the GLib main loop. Objects that outlive the activate handler are **GC-pinned** (`GCHandle` + `LifetimeRoots`) to avoid AsyncReadyCallback / GObject lifetime crashes.

### DI (`MauiProgram.CreateLinuxServiceProvider`)

Registers the same Wizionic services as Windows (SQLite stores, sync, HA, tools, themes), then:

1. `services.AddBlazorWebView(new BlazorWebViewOptions { RootComponent = typeof(Routes), HostPath = "wwwroot/index.html" })`
2. Linux browser stack: `LinuxBrowserHost`, `LinuxBrowserAgentService`, `LinuxSideBrowserService`, `LinuxBrowserOverlayService` as the `IBrowser*` implementations

`wwwroot/**` is copied to the output directory (GirCore serves the host page from disk next to the executable).

### Browser overlays (Linux-specific layout)

`LinuxBrowserHost` places the two WebKit views in a `Gtk.Overlay` over the Blazor surface. Bounds come from the same `browserInterop.js` observers as Windows (`IBrowserOverlaySync` → `LinuxBrowserOverlayService`). Layout uses **clamp + margins** so the side WebView stays in the bookmarks/app column and does not cover the chat sidebar.

`IBrowserAgentService.IsAvailable` is true only when the browser panel is open **and** a WebView is attached — same gate as Windows for BrowserAgent tools.

### Desktop integration (dock / launch bar)

`MauiIcon` does not apply without the MAUI window stack. `LinuxDesktopIcon` on startup:

1. Copies the full-res mascot (`app-appicon.png` from `Resources/AppIcon/app.png`) into `~/.local/share/icons/hicolor/*/apps/com.wizionic.app.png`
2. Writes `~/.local/share/applications/com.wizionic.app.desktop` (`Name=Wizionic`, `Icon=com.wizionic.app`, `Exec=` apphost path)
3. Sets `Gtk.Window.SetDefaultIconName` / `SetIconName` and optionally `Gdk.Toplevel.SetIconList`

GApplication id: **`com.wizionic.app`** (D-Bus / GTK require reverse-DNS). Velopack **packId** is **`Wizionic`** so artifacts are `Wizionic.AppImage` / `Wizionic-*.nupkg` (display name **Wizionic**). Local data: typically `~/.local/share/Wizionic/`.

### Build & run

```bash
# From repo root (Linux host)
dotnet build App.Maui/App.Maui.csproj -f net10.0
dotnet run --project App.Maui/App.Maui.csproj -f net10.0
# Or run the apphost:
./App.Maui/bin/Debug/net10.0/Wizionic
```

### Key Linux files

| Path | Role |
|------|------|
| `Platforms/Linux/Program.cs` | Entry point, Adwaita window, BlazorWebView, GC pins |
| `MauiProgram.cs` (`CreateLinuxServiceProvider`, `LINUX_DESKTOP` branches) | DI |
| `Services/Linux/LinuxBrowserHost.cs` | Gtk.Overlay + main/side WebKit views |
| `Services/Linux/LinuxBrowserAgentService.cs` | Main browser agent (LoadUri, EvaluateJavascript, history) |
| `Services/Linux/LinuxSideBrowserService.cs` | Side-panel WebView |
| `Services/Linux/LinuxBrowserOverlayService.cs` | Bounds / visibility from Blazor JS |
| `Services/Linux/LinuxDesktopIcon.cs` | Icon theme + .desktop + window icon |
| `App.Maui.csproj` | Multi-target, GirCore packages, Linux compile filters |

---

## Themes & MAUI UI customization

Color themes are shared across WASM and MAUI via `ThemeService` + `ThemeBootstrap.razor`:

| Piece | Location | Role |
|-------|----------|------|
| `ThemeService` | `App.Shared/Services/ThemeService.cs` | Catalog: system, light, dark, bella-purple, catppuccin-latte, dracula, github-light, nord, solarized-light |
| `ThemeInterop` | `ThemeInterop.cs` → `themeInterop.js` | `localStorage` persistence, `data-theme` on `<html>`, OS scheme listener |
| Settings UI | `SettingsPage.razor` | Theme dropdown |

**MAUI-only:** Settings also exposes **navigation bar position** (`INavLayoutState` / `NavLayoutService`) — top bar vs left vertical icon rail.

CSS variables live in `App.Shared/wwwroot/css/app.css` (theme blocks keyed by `data-theme`).

---

## Cross-Device Sync (SignalR + WebRTC)

Sync requires **email login** on both devices. The server **never** stores or relays chat/note/gallery/calendar payloads—only auth, presence, and small WebRTC signaling messages.

### Phase 1 — Presence (SignalR)
1. Authenticated client connects to `/sync-hub` (`SyncHub`, `[Authorize]`).
2. Client calls `RegisterDevice(deviceId, deviceName)`; server tracks connections in `DevicePresenceService` (in-memory).
3. Hub broadcasts `DevicesUpdated` to the user's group `user:{userId}`.
4. **`SyncPresencePage.razor`** (`/sync`) — online devices, rename, AI-server selection, per-kind auto-sync toggles (chats, notes, gallery, calendar, settings, …).

### Phase 2 — Data sync (WebRTC DataChannel)
1. Initiator (`WasmSyncService` / `MauiSyncService` + `WebRtcSyncCoordinator`) opens a WebRTC peer connection; **offer/answer/ICE** via SignalR.
2. WASM: JS `RTCPeerConnection` helpers; MAUI: SIPSorcery WebRTC.
3. **Manifest exchange** first: both sides send fingerprints (`SyncFingerprint`); only changed items transfer.
4. Encrypted content never touches the central server—JSON over the DataChannel (chunked for large blobs).
5. Receiver decrypts with the shared per-user key and writes to local stores; UI refreshes via store change events.

### Sync item kinds (`SyncItemKind`)

| Kind | Payload | Notes |
|------|---------|--------|
| `Conversation` | Encrypted chat | Password-protect flag syncs; bodies remain client-encrypted |
| `Note` | Notebook + entries | Entry-level merge (`NoteSyncMerger`) |
| `Album` | Gallery album meta | Title, protection, image id set — **no** image bytes |
| `AlbumImage` | Single gallery image | Create/update/delete; size-sensitive |
| `Calendar` | Calendar meta | Name, color, visibility |
| `CalendarEvent` | Single event | Create/update/delete |
| `Settings` | Settings category blob | See categories below; login server URL is **never** synced |
| `Bookmark` / `BookmarkFolder` / `SidebarApp` | Browser chrome (MAUI) | Desktop browser store |

### Settings sync categories (`SettingsSyncCategory`)

Exported/applied by `SettingsSyncStore` over WebRTC (`SyncItemKind.Settings`):

| Category id | Contents |
|-------------|----------|
| `local-ai` | Ollama URL, models, vision proxy, tool-routing mode/model |
| `lemonade` | Lemonade URL, key, modality defaults, models |
| `cloud-providers` | User API keys (encrypted at rest on each device) |
| `home-assistant` | HA URL/token/assistant name (desktop) |
| `tools` | Enabled MCP, MCP tokens, custom MCP URLs, OAuth connector installs/tokens |
| `system-prompt` | Custom system prompt |
| `profile` | About-you profile fields |
| `memories` | User memory list |
| `appearance` | Theme + nav layout preferences |

After local saves, `SettingsSyncHooks.AfterLocalSaveAsync` touches category timestamps so peers pick up deltas.

### Note conflict handling
Notes are notebooks of entries (`ItemId`, `ModifiedAt`, HTML body). Incoming note payloads are **not** whole-notebook overwrites:

1. `NoteSyncMerger` unions local + remote entries by `ItemId`.
2. When the same entry exists on both sides, **last-write-wins** uses the newer of `ModifiedAt` / `DeletedAt` / `Timestamp`.
3. Local-only and remote-only entries are both kept; local order is preserved and remote-only entries are appended.
4. If the merge still differs from what the peer sent, auto-sync **pushes the merged notebook back** so peers converge.
5. Open editors fold the draft into memory and re-merge on `OnNotesChanged` so unsaved typing is not wiped by a remote apply.

Same-entry concurrent HTML edits can still lose one body (LWW by time). Gallery/calendar use their own accept/merge helpers (`GallerySyncMerger`, `CalendarSyncMerger`).

### AI relay (WebRTC)
A phone/tablet without Ollama can designate another online device as **AI server**. Chat completions for that client are sent over a dedicated DataChannel (`app-ai-proxy`) to the peer running local models.

### Architecture diagram

![Cross-device sync: SignalR for signaling, WebRTC for encrypted data](/images/SyncArchitecture.png)

**Signaling path:** Device A ↔ SignalR `/sync-hub` ↔ Device B  
**Data path:** Device A ↔ WebRTC DataChannel ↔ Device B (encrypted JSON)  
**Server sees:** cookies, device IDs, SDP/ICE blobs—not chat, notes, gallery, or calendar content.

---

## Setup wizard (MAUI onboarding)

Optional first-run (and re-run from Settings) wizard on **desktop MAUI** (`SetupWizard.razor`, `ISetupWizardHost`):

| Step | Install service | Default port / role |
|------|-----------------|---------------------|
| **Home Server** | `IHomeserverInstallService` | Login website + auth host on this PC (default **`http://localhost:5150`** when installed as a service; separate SQLite DB). Dev `dotnet run` of the host project often uses **`http://localhost:5136`** (`launchSettings`) — point the app’s Login server URL at whichever is actually listening. |
| **Lemonade** | `ILemonadeInstallService` | Local multimodal AI (default **13305**) |
| **Ollama** | `IOllamaInstallService` | Local model runner (**11434**) |

Installs prefer OS services (Windows Service / systemd) when supported. Admin account creation is separate from the wizard. After install, Local AI / Login server settings are updated so the desktop client talks to localhost backends.

**Key files:** `SetupWizard.razor`, `App.Core/Homeserver/*`, `App.Core/Lemonade/ILemonadeInstallService`, `App.Core/Ollama/IOllamaInstallService`, platform install implementations under `App.Maui`.

---

## Server Database (SQLite)

| Table / entity | Purpose |
|----------------|---------|
| `Users` | Email, magic-link token, `LocalEncryptionKey` (protected) |
| `UserProviderKeys` | Optional server-stored provider API keys (importable to WASM) |
| `DataProtectionKeys` | ASP.NET key ring for encrypting secrets at rest |
| `OAuthProviders` | App-level OAuth ClientId/secret (github, google, …) for the host broker |
| `Connectors` | Marketplace catalog for OAuth/OpenAPI tiles (name, icon, scopes, featured) |

**Not stored on the central server:** WASM/MAUI conversation history, note bodies, gallery bytes, calendar events, user OAuth access tokens, or WebRTC sync payloads. Those stay on devices (KeyStore / IndexedDB / SQLite).

A **Home Server** install uses its own DB path (not overwritten by desktop app updates).

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
 | `Apis/WasmApiEndpoints.cs` | `/api/auth/*`, `/api/user/encryption-key`, `/api/keys`, `/api/tools/*` (incl. mcp-registry) |
 | `Apis/AiProxyEndpoints.cs` | `/api/proxy/providers`, `/api/proxy/chat` for CORS-restricted models |
 | `Apis/OAuthEndpoints.cs` | Host OAuth broker start/callback/session handoff |
 | `Apis/ConnectorCatalogEndpoints.cs` | Public connector marketplace catalog from SQLite |
 | `Apis/ConnectorProxyEndpoints.cs` | Authenticated OpenAPI tool proxy using user tokens |
 | `Services/MagicLinkService.cs` | Create/validate magic-link tokens |
 | `Data/AppDbContext.cs` | EF Core context for server DB |
 | `Data/OAuthProvider.cs` / `Data/Connector.cs` | OAuth app credentials + catalog rows |
 
 ### Sync & presence
 
 | File | Description |
 |------|-------------|
 | `Apis/SyncHub.cs` | SignalR hub: device registration, WebRTC signaling relay |
 | `Services/DevicePresenceService.cs` | In-memory online device registry per user |
 | `App.Core/Sync/WebRtcSyncCoordinator.cs` | Manifest/delta sync for chats, notes, gallery, calendar, settings |
 | `App.Shared/Services/SettingsSyncStore.cs` | Export/apply settings categories over WebRTC |
 
 ### Shared UI (`App.Shared`)
 
 | File | Route (approx) | Description |
 |------|---------------|-------------|
 | `Components/LoginPage.razor` | `/` | Landing, magic-link login, guest continue, login server URL |
 | `Components/ChatPage.razor` | `/chat` | Main chat UI, sidebar, attachments, streaming, Lemonade image/STT/TTS, context compact, password-protect chats |
 | `Components/NotesPage.razor` | `/notes` | Notebooks, Quill entries, floating add button |
 | `Components/GalleryPage.razor` | `/gallery` | Albums, grid, lightbox, password-protect, save-from-chat |
 | `Components/CalendarPage.razor` | `/calendar` | Multi-calendar Day/Week/Month/Year, ICS import/export |
 | `Components/SyncPresencePage.razor` | `/sync` | Device list, sync targets (incl. gallery/calendar/settings), AI server pick |
 | `Components/LocalAiPage.razor` | `/local-ai` | Ollama + Lemonade URLs, model discovery, modality defaults, tool routing model |
 | `Components/CloudProvidersPage.razor` | `/cloud-providers` | API keys for Groq, OpenRouter, Gemini, etc. |
 | `Components/SettingsPage.razor` | `/settings` | Profile, system prompt, preferences, setup wizard entry |
 | `Components/ToolsPage.razor` | `/tools` | Installed + Discover (OAuth catalog + MCP registry), install/connect |
 | `Components/SetupWizard.razor` | (overlay) | MAUI: optional Home Server / Lemonade / Ollama install |
 | `Components/HomeAssistantPage.razor` | `/home-assistant` | HA URL, token, wake word, device list (MAUI) |
 | `Components/EmbeddedBrowser.razor` | (in `/chat` split) | Embedded browser chrome, PWA toolbar (MAUI) |
 | `Components/ThemeBootstrap.razor` | (layout) | Applies saved theme on load |
 | `Layout/AppLayout.razor` | - | Main cohesive layout for both WASM & MAUI |
 | `Layout/AppTopBar.razor` | - | Browser toggle, HA nav link (MAUI) |
 
 ### Shared Logic (`App.Shared`)
 
 | File | Description |
 |------|-------------|
 | `Services/ChatCompletionService.cs` | Core completion loop, streaming, tool routing, context trim, vision proxy |
 | `Services/ChatModelCatalogService.cs` | Manage available AI models (Ollama, Lemonade, proxied, user keys) |
 | `Services/Lemonade/LemonadeImageService.cs` | Lemonade image generate / edit / upscale |
 | `Services/Lemonade/LemonadeSpeechService.cs` | Lemonade STT + TTS |
 | `Services/Lemonade/OmniMediaExtractor.cs` | Extract data-URI media from Omni replies |
 | `Services/Mcp/McpToolSource.cs` | Discover and cache MCP tools from enabled servers |
 | `Services/Connectors/OpenApiConnectorToolSource.cs` | Curated OpenAPI tools for installed OAuth connectors |
 | `Services/Tools/NativeToolModule.cs` | Host-proxied built-in tools (`search_web`, weather, etc.) |
 | `Services/Tools/NotesToolModule.cs` | Notebook AI tools |
 | `Services/Tools/GalleryToolModule.cs` | Gallery AI tools |
 | `Services/Tools/CalendarToolModule.cs` | Calendar AI tools |
 | `Services/Tools/LemonadeToolModule.cs` | Client-side Lemonade modality tools for ME.AI |
 | `Services/Tools/CompositeToolProvider.cs` | Composes `IToolModule` + MCP + connectors |
 | `Services/Tools/CompositeRequestRouter.cs` | Rules / AI / Hybrid entry point |
 | `Services/Tools/ContextualRequestRouter.cs` | Keyword / wake-word / panel heuristics |
 | `Services/Tools/AiRequestRouter.cs` | Small-model module classifier |
 | `Services/SettingsSyncStore.cs` | Settings category export/import for WebRTC |
 
 ### Business Contracts (`App.Core`)
 
 | File | Description |
 |------|-------------|
 | `Storage/IConversationStore.cs` | Chat history persistence + optional password-protect flag |
 | `Storage/INoteStore.cs` | Notes persistence + password-protect flag |
 | `Storage/IGalleryStore.cs` | Albums, thumbs, encrypted images, display URLs |
 | `Storage/ICalendarStore.cs` | Calendars + events |
 | `Storage/ICryptoService.cs` | Interface for AES-GCM encryption/decryption |
 | `Storage/IKeyStore.cs` | Settings, Ollama/Lemonade, API keys, MCP, OAuth installs, tool routing |
 | `Chat/IChatCompletionService.cs` | Completion contract + `ChatCompletionStats` |
 | `Chat/ChatModelInfo.cs` | Catalog entry (tools, vision, context, Omni, image flags) |
 | `Tools/ToolRoutingMode.cs` | Rules / Ai / Hybrid |
 | `Lemonade/LemonadeModelCatalogResolver.cs` | Lemonade `/v1/models` → settings |
 | `Ollama/OllamaCapabilitiesResolver.cs` | Ollama show + OpenAI-compat fallback |
 | `Sync/ISyncService.cs` | Interface for cross-device synchronization |
 | `Sync/SyncItemKind.cs` | Conversation, Note, Album, Calendar, Settings, … |
 | `Sync/SettingsSyncCategory.cs` | Stable settings blob ids |
 | `Homeserver/IHomeserverInstallService.cs` | Desktop Home Server install |
 | `SmartHome/ISmartHomeService.cs` | Home Assistant REST client contract |
 | `Browser/IBrowserAgentService.cs` | Embedded WebView navigation & script eval |
 | `Browser/IBrowserContext.cs` | Agent tool bridge for browser control |
 | `Tools/IRoutingSessionStore.cs` | Per-conversation HA follow-up session (15 min TTL) |
 | `UI/IAppNavigation.cs` | Cross-page navigation (e.g. OAuth return → Tools) |
 
 ### Client Implementations (WASM vs MAUI)
 
 | Feature | WASM Implementation (`App.Client`) | MAUI Implementation (`App.Maui`) |
 |---------|-------------------------------------------|------------------------------------------|
 | **Conversations** | `Services/WasmConversationStore.cs` (IndexedDB) | `Services/SqliteConversationStore.cs` (SQLite) |
 | **Notes** | `Services/WasmNoteStore.cs` (IndexedDB) | `Services/SqliteNoteStore.cs` (SQLite) |
 | **Gallery** | WASM gallery store (IndexedDB + JS encrypt/thumbs) | SQLite gallery store |
 | **Calendar** | WASM calendar store (IndexedDB) | SQLite calendar store |
 | **Encryption** | `Services/WasmCryptoService.cs` (WebCrypto JS) | `Services/MauiCryptoService.cs` (Native .NET) |
 | **Sync** | `Services/WasmSyncService.cs` | `Services/MauiSyncService.cs` (SIPSorcery) |
 | **Keys/Settings** | `Services/WasmKeyStore.cs` (localStorage) | `Services/SqliteKeyStore.cs` (SQLite) |
 | **Home Assistant** | `NullSmartHomeService` (no-op) | `Services/HomeAssistantService.cs` |
 | **OAuth / URI** | Browser navigation | `MauiUriLauncher`, `MauiOAuthInterceptor` |
 | **Embedded browser** | Null browser services (`NullBrowserAgentService`, etc.) | **Windows:** `MauiBrowserAgentService`, `BrowserOverlayService`, WebView2. **Linux:** `LinuxBrowserAgentService`, `LinuxBrowserHost`, WebKitGTK. Shared: `SqliteBrowserStore`, `MauiPwaDetector`. |


---

## Typical Agent Onboarding
 
 1. Read this doc and skim `wwwroot/ROADMAP.md` for direction (not current state).
 2. For **chat/AI** changes → `ChatPage.razor`, `ChatCompletionService` (Shared), `ChatModelCatalogService`.
 3. For **tool routing (Rules / AI / Hybrid)** → `CompositeRequestRouter`, `AiRequestRouter`, `ContextualRequestRouter`, `IKeyStore.ToolRoutingMode` / `ToolRoutingModelId`.
 4. For **Lemonade (image / STT / TTS / Omni)** → `LocalAiPage.razor`, `LemonadeImageService`, `LemonadeSpeechService`, `OmniMediaExtractor`, `LemonadeToolModule`, `LemonadeModelCatalogResolver`.
 5. For **streaming / stats / context compact** → `ChatCompletionService`, `ChatCompletionStats`, `ChatPage.razor` context button.
 6. For **Notes** → `NotesPage.razor`, `INoteStore`, `NotesToolModule`, `NoteSyncMerger` / `INotesSyncBridge`.
 7. For **Gallery** → `GalleryPage.razor`, `IGalleryStore`, `GalleryToolModule`, `IConversationMediaBuffer`, gallery sync kinds.
 8. For **Calendar** → `CalendarPage.razor`, `ICalendarStore`, `CalendarToolModule`, `CalendarIcs`, calendar sync kinds.
 9. For **storage/privacy** → Core store interfaces + `App.Client` / `App.Maui` implementations (encryption + password-protect flags).
 10. For **vision proxy / model routing** → `LocalAiPage.razor`, `ChatCompletionService`, `WasmKeyStore`/`SqliteKeyStore`.
 11. For **sync (content + settings)** → `ISyncService`, `WebRtcSyncCoordinator`, `SettingsSyncStore`, `SyncPresencePage.razor`, platform sync services.
 12. For **new API endpoints** → `WasmApiEndpoints.cs`, `AiProxyEndpoints.cs`, OAuth/connector APIs; register in host `Program.cs`.
 13. For **tools / MCP / OAuth connectors** → `ToolsPage.razor`, `McpToolSource`, `OpenApiConnectorToolSource`, `CompositeToolProvider`, OAuth broker endpoints, SQLite `Connectors` / `OAuthProviders`.
 14. For **setup wizard / Home Server / local installers** → `SetupWizard.razor`, `IHomeserverInstallService`, Lemonade/Ollama install services (MAUI).
 15. For **Home Assistant** → `ISmartHomeService` (Core), `HomeAssistantPage.razor`, `HomeAssistantToolModule`, routers, `ChatCompletionService`.
 16. For **embedded browser (Windows)** → `MainPage.xaml`, `MauiBrowserAgentService`, `BrowserOverlayService`, `BrowserAgentToolModule`, `EmbeddedBrowser.razor`, `browserInterop.js`.
 17. For **embedded browser / shell (Linux)** → `Platforms/Linux/Program.cs`, `Services/Linux/*`, `WebKit.BlazorWebView.GirCore`, `MauiProgram.CreateLinuxServiceProvider`, section [Linux Desktop](#linux-desktop-maui-project-net100).
 18. For **themes / MAUI chrome** → `ThemeService`, `themeInterop.js`, `SettingsPage.razor`, `NavLayoutService`.


---

*Last updated: August 2026 — Gallery + Calendar (UI, encrypted stores, AI tool modules, WebRTC sync); Notes/Gallery/Calendar as native AI tools; Rules/AI/Hybrid tool router (`CompositeRequestRouter`); settings sync categories (Local AI, Lemonade, cloud keys, HA, tools/MCP/OAuth, profile, memories, appearance); Tools page marketplace (official MCP registry search + DB OAuth catalog, host OAuth broker); MAUI setup wizard (Home Server / Lemonade / Ollama); prior Lemonade dual local AI, HA, embedded browser (Windows WebView2 + Linux GirCore), themes.*