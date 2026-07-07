# Chatfish Architecture

**Purpose:** Quick reference for humans and AI agents working on this codebase. Describes what exists today (not the future roadmap). For planned work see [ROADMAP.md](/roadmap).

**Stack:** .NET 10 · Blazor Web App (Auto: server shell + Interactive WebAssembly) · Blazor Hybrid (MAUI) · SQLite · SignalR · WebRTC · Microsoft.Extensions.AI · WebView2 (MAUI browser)

---

## Core Values

- **Privacy-first** — Chat history and notes live in the browser (IndexedDB), encrypted at rest. The server does not store conversation content for the WASM path.
- **Local AI** — Ollama on the user's machine is a first-class provider. A logged-in device can relay AI to other devices over WebRTC.
- **Login is optional** — Guests can chat and take notes immediately. Email + magic link is only needed for cross-device sync and encrypted key distribution.
- **Minimal server footprint** — Server handles auth, signaling, tool proxies (CORS), and CORS-restricted AI proxies. Heavy lifting runs in the browser.
- **Tool-rich agents** — Built-in web search / URL summarization plus user-selected MCP servers, wired through `Microsoft.Extensions.AI` function calling. On MAUI, modular tools also control Home Assistant devices and an embedded browser.
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

Tools are composed by `CompositeToolProvider` from injectable **`IToolModule`** implementations plus cached MCP tools. Each module exposes `ModuleName`, `IsAvailable`, and a list of `AITool` functions via `Microsoft.Extensions.AI`.

| Module | Tools | Where it runs | Availability |
|--------|-------|---------------|--------------|
| **Native** (`NativeToolModule`) | `search_web`, `summarize_url`, `get_time`, `calculate`, `get_current_weather` | Server via `POST /api/tools/*` | Always |
| **HomeAssistant** (`HomeAssistantToolModule`) | `ListLights`, `ControlLight`, `GetEntityState`, `CallService` | MAUI → direct LAN HTTP to Home Assistant | MAUI only, when HA configured |
| **BrowserAgent** (`BrowserAgentToolModule`) | `navigate_to`, `get_page_content`, `click_element`, `fill_field` | MAUI → native WebView JS eval | MAUI only, when browser panel open |
| **MCP servers** (`McpToolSource`) | User-enabled remote tools | Client calls MCP HTTP directly | When servers enabled |

**Routing:** Before each completion, `ContextualRequestRouter` classifies the last user message and may narrow the tool set to a single module (see [Home Assistant](#home-assistant-maui) and [Embedded Browser](#embedded-browser-maui)). `ChatCompletionService` records the route in tool traces (`🧭 Route: …`) and appends module-specific system instructions when needed.

On WASM, `HomeAssistantToolModule` and `BrowserAgentToolModule` are not registered; `NullSmartHomeService` and null browser services satisfy the Core interfaces but expose no agentic tools.

Tool execution traces are shown in the chat UI (`ToolExecutionTrace`). Models that support function calling get an automatic multi-turn tool loop via `UseFunctionInvocation`.

---

## Home Assistant (MAUI)

Chatfish can agentically control a local Home Assistant instance from the MAUI desktop app. Configuration lives at `/home-assistant` (`HomeAssistantPage.razor`); the chat window drives devices once a long-lived access token and base URL are saved.

### Configuration & storage

| Setting | Stored in | Purpose |
|---------|-----------|---------|
| Base URL | `IKeyStore.HomeAssistantBaseUrl` | e.g. `http://192.168.4.23:8123` |
| Long-lived token | `IKeyStore.HomeAssistantToken` | Bearer token from HA Profile → Security |
| Assistant name (wake word) | `IKeyStore.HomeAssistantAssistantName` | Default `Home` — user addresses this name in chat |
| Device summary cache | `IKeyStore.HomeAssistantDeviceSummary` | Cleartext list of `light.*` entities refreshed on save/test |

Credentials are normalized by `HomeAssistantCredentials` (Core) and persisted in SQLite via `SqliteKeyStore` (`HomeAssistantConfig` DTO). The settings page calls `ISmartHomeService.TestConnectionAsync` and `ListLightEntitiesAsync` to validate and refresh the device list.

### Core contracts

| Interface / type | Location | Role |
|------------------|----------|------|
| `ISmartHomeService` | `ChatfishApp.Core/SmartHome/` | `TestConnectionAsync`, `CallServiceAsync`, `GetEntityStateAsync`, `ListLightEntitiesAsync` |
| `HomeAssistantCredentials` | `ChatfishApp.Core/SmartHome/` | URL/token normalization |
| `HomeAssistantConfig` | `ChatfishApp.Core/Storage/` | DTO for key store persistence |

### MAUI implementation

`HomeAssistantService` (MAUI) is a direct LAN `HttpClient` — calls never go through the Chatfish server or browser DevTools. It hits standard HA REST endpoints (`/api/`, `/api/states`, `/api/services/{domain}/{service}`) with `Authorization: Bearer {token}`. Proxy is disabled (`UseProxy = false`) to avoid LAN hangs.

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
| `ListLights` | List `light.*` entities with friendly names | "Home, what lights do you know?" |
| `ControlLight` | Turn on/off, brightness (0–255), color name or hex | "Home, turn off the kitchen light" / "make it blue" |
| `GetEntityState` | Read any entity state JSON | "Home, is the garage door open?" |
| `CallService` | Generic `domain.service` with JSON `service_data` | Scenes, switches, climate, etc. |

**Key files:** `HomeAssistantPage.razor`, `HomeAssistantService.cs`, `HomeAssistantToolModule.cs`, `ContextualRequestRouter.cs`, `ChatCompletionService.cs` (`BuildHomeAssistantPrompt`, `RecordHomeAssistantSessionIfNeeded`)

---

## Embedded Browser (MAUI)

The MAUI chat page can show a split view: chat on the left, embedded browser on the right. Toggle via the globe icon in `AppTopBar.razor` (`IBrowserPanelState`). When open, the model can navigate, read page text, click elements, and fill form fields agentically.

### WebView architecture (hybrid shell + native overlay)

Chatfish uses a **two-layer** pattern on Windows:

1. **Blazor Hybrid shell** — `BlazorWebView` in `MainPage.xaml` renders all Razor UI (chat, toolbar, browser chrome).
2. **Native WebView overlays** — two `Microsoft.Maui.Controls.WebView` controls (`browserWebView`, `browserSideWebView`) sit in the same `AbsoluteLayout`, positioned on top of placeholder `<div>` hosts in the Blazor DOM.

On **Windows**, MAUI's `WebView` maps to **WebView2** (Chromium/Edge). `BrowserWebViewPlatformService` configures the underlying `CoreWebView2` for new-window behavior, download prompts, and clear-on-exit. This is **not** an in-DOM `<iframe>` — it is a **native platform WebView** overlaid at pixel coordinates reported from JavaScript.

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
| `IBrowserAgentService` | `ChatfishApp.Core/Browser/` | Navigation, history, `EvaluateScriptAsync`, page text/HTML |
| `IBrowserContext` | `ChatfishApp.Core/Browser/` | Agent tool bridge (`NavigateAsync`, `GetPageContentAsync`, `ClickElementAsync`, `FillFieldAsync`) |
| `IBrowserStore` | `ChatfishApp.Core/Browser/` | Bookmarks, history, settings (SQLite on MAUI) |
| `IBrowserSidebarStore` | `ChatfishApp.Core/Browser/` | Pinned apps / vertical toolbar entries |
| `IBrowserPanelState` | `ChatfishApp.Core/UI/` | Browser panel open/closed, chat column width |
| `IBrowserSidePanelState` | `ChatfishApp.Core/UI/` | Side panel content (bookmarks, settings, web app) |
| `IBrowserOverlaySync` | `ChatfishApp.Core/Browser/` | Native overlay bounds + visibility |
| `IBrowserSideAgentService` | `ChatfishApp.Core/Browser/` | Side-panel WebView navigation |
| `IPwaDetector` | `ChatfishApp.Core/Browser/` | PWA manifest detection for install/pin |

### MAUI implementations

| Service | File | Role |
|---------|------|------|
| `MauiBrowserAgentService` | `MauiBrowserAgentService.cs` | Main WebView: `WebView.Source`, `EvaluateJavaScriptAsync` for agent actions |
| `MauiSideBrowserService` | `MauiSideBrowserService.cs` | Side-panel WebView |
| `MauiBrowserContext` | `MauiBrowserContext.cs` | `IBrowserContext` — available when panel open **and** WebView attached |
| `BrowserAgentToolModule` | `BrowserAgentToolModule.cs` | Exposes agent tools to the model |
| `BrowserOverlayService` | `BrowserOverlayService.cs` | Positions native WebViews |
| `BrowserWebViewPlatformService` | `BrowserWebViewPlatformService.cs` | WebView2 platform hooks (Windows) |
| `SqliteBrowserStore` / `SqliteBrowserSidebarStore` | MAUI Services | Persistent bookmarks, history, pinned apps |

Wiring happens in `MainPage.xaml.cs` on load: `agent.AttachWebView(browserWebView)`, `overlay.Initialize(...)`, `platform.Attach(browserWebView)`.

### JS interop (`browserInterop.js`)

JS is used for **layout and drag UX**, not for loading web pages:

| JS function | Called from | Purpose |
|-------------|-------------|---------|
| `chatfishBrowser.startBoundsObserver` | `EmbeddedBrowser.razor` | `ResizeObserver` → `[JSInvokable] OnBrowserMainOverlayBounds` / `OnBrowserSideOverlayBounds` |
| `chatfishBrowser.reportBoundsNow` | Overlay refresh | Force bounds recalc after dialogs close |
| `chatfishBrowser.startSplitterDrag` | Chat/browser split | Resize chat column |
| `chatfishBrowser.startSidePanelSplitterDrag` | Side panel split | Resize bookmarks/web side column |
| `chatfishBrowser.startBookmarkBarDrag` / `startSidebarDrag` / `startVtoolbarDrag` | Bookmark & PWA toolbar | Reorder via drag-drop |
| `chatfishBrowser.getWrapperWidth` / `getPanelAnchor` | Layout helpers | Split width, context menu positioning |

Agentic page interaction uses **`WebView.EvaluateJavaScriptAsync`** in C# (`MauiBrowserAgentService`): `document.querySelector`, `click()`, `innerText`, etc.

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

The right-hand `browser-vtoolbar` lists pinned apps from `SidebarStore`. `MauiPwaDetector` watches navigation and detects `<link rel="manifest">` via in-page JS + HTML parse + HTTP guesses. When a manifest is found, the **+** button offers **Install app** (PWA metadata: name, icons, `start_url`, `display`, theme colors) or **Pin page only**. PWAs open in the side panel or main browser per `OpenTarget` (configurable via context menu). Drag-reorder uses `chatfishBrowser.startVtoolbarDrag`.

**Key files:** `EmbeddedBrowser.razor`, `ChatPage.razor`, `browserInterop.js`, `MainPage.xaml`, `MauiBrowserAgentService.cs`, `BrowserAgentToolModule.cs`, `MauiPwaDetector.cs`, `ContextualRequestRouter.cs`

---

## Themes & MAUI UI customization

Color themes are shared across WASM and MAUI via `ThemeService` + `ThemeBootstrap.razor`:

| Piece | Location | Role |
|-------|----------|------|
| `ThemeService` | `ChatfishApp.Shared/Services/ThemeService.cs` | Catalog: system, light, dark, bella-purple, catppuccin-latte, dracula, github-light, nord, solarized-light |
| `ThemeInterop` | `ThemeInterop.cs` → `themeInterop.js` | `localStorage` persistence, `data-theme` on `<html>`, OS scheme listener |
| Settings UI | `SettingsPage.razor` | Theme dropdown |

**MAUI-only:** Settings also exposes **navigation bar position** (`INavLayoutState` / `NavLayoutService`) — top bar vs left vertical icon rail.

CSS variables live in `ChatfishApp.Shared/wwwroot/css/chatfish.css` (theme blocks keyed by `data-theme`).

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
 | `Components/HomeAssistantPage.razor` | `/home-assistant` | HA URL, token, wake word, device list (MAUI) |
 | `Components/EmbeddedBrowser.razor` | (in `/chat` split) | Embedded browser chrome, PWA toolbar (MAUI) |
 | `Components/ThemeBootstrap.razor` | (layout) | Applies saved theme on load |
 | `Layout/AppLayout.razor` | - | Main cohesive layout for both WASM & MAUI |
 | `Layout/AppTopBar.razor` | - | Browser toggle, HA nav link (MAUI) |
 
 ### Shared Logic (`ChatfishApp.Shared`)
 
 | File | Description |
 |------|-------------|
 | `Services/ChatCompletionService.cs` | Core completion loop + tool execution logic |
 | `Services/ChatModelCatalogService.cs` | Manage available AI models across providers |
 | `Services/Mcp/McpToolSource.cs` | Discover and cache MCP tools from enabled servers |
 | `Services/Tools/NativeToolModule.cs` | Server-proxied built-in tools (`search_web`, weather, etc.) |
 | `Services/Tools/CompositeToolProvider.cs` | Composes `IToolModule` + MCP tools |
 | `Services/Tools/ContextualRequestRouter.cs` | Wake-word / session / browser-panel routing |
 
 ### Business Contracts (`ChatfishApp.Core`)
 
 | File | Description |
 |------|-------------|
 | `Storage/IConversationStore.cs` | Interface for chat history persistence |
 | `Storage/INoteStore.cs` | Interface for notes persistence |
 | `Storage/ICryptoService.cs` | Interface for AES-GCM encryption/decryption |
 | `Sync/ISyncService.cs` | Interface for cross-device synchronization |
 | `SmartHome/ISmartHomeService.cs` | Home Assistant REST client contract |
 | `Browser/IBrowserAgentService.cs` | Embedded WebView navigation & script eval |
 | `Browser/IBrowserContext.cs` | Agent tool bridge for browser control |
 | `Tools/IRoutingSessionStore.cs` | Per-conversation HA follow-up session (15 min TTL) |
 
 ### Client Implementations (WASM vs MAUI)
 
 | Feature | WASM Implementation (`ChatfishApp.Client`) | MAUI Implementation (`ChatfishApp.Maui`) |
 |---------|-------------------------------------------|------------------------------------------|
 | **Conversations** | `Services/WasmConversationStore.cs` (IndexedDB) | `Services/SqliteConversationStore.cs` (SQLite) |
 | **Notes** | `Services/WasmNoteStore.cs` (IndexedDB) | `Services/SqliteNoteStore.cs` (SQLite) |
 | **Encryption** | `Services/WasmCryptoService.cs` (WebCrypto JS) | `Services/MauiCryptoService.cs` (Native .NET) |
 | **Sync** | `Services/WasmSyncService.cs` | `Services/MauiSyncService.cs` |
 | **Keys/Settings** | `Services/WasmKeyStore.cs` (localStorage) | `Services/SqliteKeyStore.cs` (SQLite) |
 | **Home Assistant** | `NullSmartHomeService` (no-op) | `Services/HomeAssistantService.cs` |
 | **Embedded browser** | Null browser services (`NullBrowserAgentService`, etc.) | `MauiBrowserAgentService`, `BrowserOverlayService`, `SqliteBrowserStore` |


---

## Typical Agent Onboarding
 
 1. Read this doc and skim `wwwroot/ROADMAP.md` for direction (not current state).
 2. For **chat/AI** changes → `ChatPage.razor`, `ChatCompletionService` (Shared), `ChatModelCatalogService`.
 3. For **storage/privacy** → `IConversationStore`/`INoteStore` (Core) and the respective implementations in `ChatfishApp.Client` or `ChatfishApp.Maui`.
 4. For **vision proxy / model routing** → `LocalAiPage.razor`, `ChatCompletionService`, `WasmKeyStore`/`SqliteKeyStore`.
 5. For **sync** → `ISyncService` (Core), `SyncPresencePage.razor`, and platform implementations of `ISyncService`.
 6. For **new API endpoints** → `WasmApiEndpoints.cs` or `AiProxyEndpoints.cs`; register in host `Program.cs`.
 7. For **tools/MCP** → `NativeToolModule`, `CompositeToolProvider`, `McpToolSource`, `ToolsPage.razor`.
 8. For **Home Assistant** → `ISmartHomeService` (Core), `HomeAssistantPage.razor`, `HomeAssistantToolModule`, `ContextualRequestRouter`, `ChatCompletionService`.
 9. For **embedded browser** → `IBrowserAgentService` / `IBrowserContext` (Core), `EmbeddedBrowser.razor`, `browserInterop.js`, `MainPage.xaml`, `BrowserAgentToolModule`.
 10. For **themes / MAUI chrome** → `ThemeService`, `themeInterop.js`, `SettingsPage.razor`, `NavLayoutService`.


---

*Last updated: July 2026 — reflects WASM local-storage architecture plus MAUI Home Assistant, embedded browser, themes, and PWA toolbar.*