# Chatfish.me Project Roadmap

**Version:** 3.0 (July 2026)  
**Developer:** Daniel Goodwin — Senior Web Application Developer (25+ years experience)  
**Project Status:** Active personal / resume project

## Project Vision & Goals

Chatfish.me is a **privacy-first, local-first AI chat hub** that lets you chat with multiple AI models while keeping conversations in the browser.  

Core Goals
1) Privacy : Keep chat history encrypted and private 
2) Email login is only needed for syncing chats/settings privately between browsers and devices
2) Local :  Focus on allowing local chat through the users device as this is the most private
3) Low cost Cloud AI :  When cloud AI services are used, emphasize the ones that are free or low cost
4) Keep Hosting costs down :  Store as little as possible on the server and use client technologies where possible
5) Frictionless setup :  Some configuration is needed for setup but always make it is easy as possible to start using

 It is delivered through three distinct parts:

1. **Pure Blazor interactive server app** — the convenient hosted experience (current foundation).
2. **Blazor WebAssembly** — full browser-native local client.
3. **.NET MAUI with Blazor Hybrid** — native desktop/mobile apps with deep WebView integration for agentic control.

It supports local models (Ollama, especially seamless in local targets), cloud providers via API keys (OpenRouter, Gemini, generic OpenAI-compatible), and will add advanced features like cross-device sync *only among your local clients* (with chat history stored locally in the WASM and MAUI targets, never persisted on the central server for those), MCP tools, and rich AI visibility into browser tabs (powerfully enabled via the embedded WebView control in the hybrid target) — all while keeping server resource usage minimal for the hosted option (where server-side history tied to accounts may be used for convenience).

Primary goals:
- Strong recent multi-LLM and AI tooling development experience for resume
- Genuinely useful tool for myself, family, and eventually the public
- Privacy-first (conversations stay in browser LocalStorage / device storage by default in the WASM and MAUI targets; sync only to local clients, never saved on the central server for local targets. The hosted server target may use server-side storage tied to logins for convenience.)
- Minimal server load (cheap hosting friendly)
- Built with modern Blazor best practices and maximum reuse across targets
- Excellent context management and user awareness of token usage
- Support multiple delivery targets from one shared component/service core

---

## Core Shared Capabilities

**Goal:** Features and foundations that apply (or will apply) across all three delivery targets. A core guiding principle for the local-first targets (WASM and MAUI): **syncing of chat history only to local clients (never saved on the central server)**. The pure server hosted target prioritizes convenience with account-tied server storage.

### Multi-Provider Support (Highest Priority)

**Goal:** Allow users to easily chat with many different models without being locked into one provider.

**Key Tasks**
- Add Ollama support (local models, auto-detect available models)
- ~~Add OpenRouter integration (via user-provided API key)~~ — delivered (see OpenRouter provider + attribution headers + many models including tool-calling ones)
- ~~Add Google Gemini Flash 2.0 support~~ — delivered (gemini-2.5-flash via compat; notes on new projects + free tier without billing)
- Add generic OpenAI-compatible provider (base URL + API key) so users can connect to any compatible endpoint (Groq already exists, add others)
- Clean model selector UI that groups models by provider
- Per-provider connection status and API key management (secure storage)

**Why first?** Immediate value and flexibility. Users can start using powerful free/local models right away. Ollama will be especially seamless and the default in targets 2 and 3.

**Dependencies:** None  
**Estimated effort:** 4–7 days

### Context Length Management & Intelligence

**Goal:** Make users aware of context usage and gracefully handle limits.

**Key Tasks**
- Store maximum context length (in tokens) for every supported model
- Track approximate token usage per conversation (using a tokenizer or good approximation)
- Display context usage visually in the UI (e.g., progress bar or "12k / 128k tokens used")
- When approaching limit:
  - Show warning
  - Offer to auto-summarize older messages
  - Suggest starting a new conversation
- Implement conversation summarization (using the current model or a cheaper one)
- Persist summary + recent messages when summarizing

**Why important?** One of the biggest pain points with long chats is hitting the context wall unexpectedly. This feature will be very user-friendly and impressive on a resume. Token tracking matters for local models (varying context windows) as much as cloud ones.

**Dependencies:** Phase 1 / Core multi-provider (need model metadata)  
**Estimated effort:** 5–8 days

### Tool Use, Web Search & Agentic Behavior (incl. Jina URL Summarization)

**Goal:** Let *models* autonomously use tools (web search, page summarization, etc.) when they need external or up-to-date information — instead of only the user explicitly asking.

**Key Tasks (delivered / in progress)**
- Wire Microsoft.Extensions.AI function/tool calling (`UseFunctionInvocation` + `AIFunctionFactory`) into the chat flow so the model can emit tool calls, we execute them (C#), feed results back, and repeat until a final answer.
- Implement core free tools the model can call on demand:
  - `web_search(query)` — free DuckDuckGo-backed search returning titles/links/snippets (model can then decide to dig deeper).
  - `summarize_url(url)` — Jina Reader (`r.jina.ai`) for clean, LLM-friendly page content/summary. Directly realizes the original "Jina URL Summarization" idea but now model-driven.
  - Bonus always-free tool: `get_current_time_utc` (and easy to add more: calculator, etc.).
- Tools are app-level (no extra user keys) and work with any tool-calling-capable model (excellent support on OpenRouter for Claude, GPT-4o, Llama 3.3/4, Gemini, Mistral, etc.).
- The existing per-user key + catalog system makes this available the moment a suitable key (especially OpenRouter) is configured.
- UI: Small "tools enabled" hint near the model selector. Tool execution is "behind the scenes" for v1 (final answer benefits); future work can surface traces ("Model used web_search...").
- Logging + error handling for tool calls/failures (graceful fallback to the model's knowledge).

**Why here?** This is the natural evolution after multi-provider (Phase 1 / Core). It directly delivers "the model can search the web etc. when it needs to." It also makes the original Jina Phase 3 far more powerful (model decides to use the summarizer). Ties beautifully into later MCP / browser awareness (more tools the model can autonomously invoke). Keeps the "completely free models" spirit by using free backends (DDG + Jina free tier). Target-specific tools (e.g. browser tab tools) will be added in part 3.

**Dependencies:** Phase 1 / Core (pluggable IChatClient + per-user keys + OpenRouter for great tool-calling model selection)  
**Estimated effort:** 3–6 days (core wiring + 2 tools + UI hints + docs)

**Future extensions in this area**
- More tools (user files, code interpreter sandbox if safe, calendar, etc.).
- Surface tool traces in chat history / "thinking" steps.
- Real streaming + live tool events (instead of post-full-response fake stream).
- Advanced agent patterns (memory, planning, multi-step workflows, handoffs) — .NET equivalent of what OpenRouter's Agent SDK provides for TS/Python.
- Per-convo "agent mode" toggle or budget/limits for tool usage.
- Integration with Phase 5 / MCP concepts (models can discover and call MCP tools).

### Multimodal / Vision Support (Future)

**Goal:** Allow users to upload, paste, or drag images (and eventually documents/PDFs) so that vision-capable models can "see" them and answer questions about the content.

**Key Tasks**
- Add image upload / paste / drag-and-drop support in the chat input area (preview thumbnails, remove button).
- Extend the message model / history to carry image attachments (store as base64 or local file refs for now; later encrypted localstorage in WASM/MAUI targets).
- Add `SupportsVision` flag to `ModelDefinition` in the catalog (mark models like GPT-4o, Gemini 2.x, Claude 3+, Llama 3.2 vision, etc. on OpenRouter).
- When building `ChatMessage` list for a vision model, include `ImageContent` (or DataContent) using Microsoft.Extensions.AI types.
- Only show the image upload UI (or enable it) when the currently selected model supports vision.
- Handle provider-specific details (some use base64 data URLs, some need special content parts).
- Graceful fallback / error if a non-vision model is chosen with images attached.
- Optional: basic document support (e.g. extract text from PDF/images via OCR or simple libs for text PDFs). Native camera/file access in MAUI target will enhance this.

**Why here?** Many modern models on OpenRouter and elsewhere are multimodal. This is a high-value, resume-friendly feature that builds directly on the multi-provider + tool foundation. It enables use cases like "describe this screenshot", "analyze this chart", "read this whiteboard photo", "summarize this PDF page", etc. Fits nicely before or alongside full MCP/browser awareness. Storage and capture will be local-first in targets 2 and 3.

**Dependencies:** Phase 1 / Core (pluggable providers + catalog to mark vision models), good error handling for unsupported cases.
**Estimated effort:** 4–8 days (UI upload + storage + ME.AI content parts + catalog flag + provider quirks).

---

## 1. Pure Blazor Interactive Server App (Current / Hosted Target)

**Current State:** The active, working implementation (the code you run with `dotnet run` today). Single-project .NET 10 Blazor Web app (ChatfishApp.csproj) using primarily `InteractiveServerRenderMode` everywhere (with `AddInteractiveWebAssemblyComponents` registered and `UseWebAssemblyDebugging` present for future, but client render mode not mapped and Routes.razor forces server). Full server-side SQLite via EF Core (`chatfish.db`) for Users, Conversations, Messages, and UserProviderKeys. Magic-link only auth (cookie based). Modern pluggable AI via `Microsoft.Extensions.AI` + OpenAI SDK compat (catalog-driven for Groq/Gemini/OpenRouter). Server-executed app-level tools (web_search via DDG, summarize_url via Jina, time) wired with `UseFunctionInvocation`. Limited JS interop (only for textarea/scroll UX). No client storage yet. Roadmap.razor renders this md.

**Goal:** Continue to provide a convenient, always-on hosted / web experience (easy magic-link access for family etc.). For this target, server-side chat history tied to user accounts (via the existing SQLite + EF) provides useful cross-device access through login, while the stronger local-only storage (never on server) is delivered in the WASM and MAUI targets.

**Guiding Rule for this target:** The hosted server experience can use server-persisted chats (current model: Conversations and Messages in DB per user) for convenience and multi-browser access via the magic-link login. True local-only chat history (browser/device storage only, never saved on the central server) is a primary goal of the WASM phase (target 2) and MAUI (target 3). Server DB remains for Users and ProviderKeys (and optionally chats for this hosted mode).

**Key Tasks (new + adapted from prior work)**
- Introduce storage and context abstractions to remove tight coupling for *keys and user identity* (current services rely on `IHttpContextAccessor` + direct EF `ChatfishDbContext`): `IUserContext`, `IKeyStore`. (Full `IChatHistoryStore` with local impls will be introduced in the WASM phase.) Refactor `ProviderKeyService` and related flows as needed. Keep chat history using the existing server-side `ConversationService` + DB for this target (for hosted convenience).
- Keep and enhance what already works well for hosted: magic-link login flow + logout endpoints, per-user keys + enable/disable in Settings (with good provider guidance), clean grouped model selector, "tools enabled" hints, full agentic tool execution on the server (powerful, no browser sandbox/CORS limits), context management UI (when built), vision upload (when built). Server-side chat persistence (current Conversations/Messages) remains for account-linked access.
- Maintain minimal server resource usage and cheap-hosting friendliness.

**Why here?** This *is* the current foundation that already delivers the multi-provider (partial), tool/agentic, and login work with server-side chat history for convenient hosted/multi-user access. It provides immediate usable value and a stable base while the stronger local targets (with fully local chat history) are built on the shared abstractions and components. Hosted convenience (login, central keys, cross-"device" via account) remains useful for some users; the no-server-save local history comes in WASM and MAUI.

**Dependencies:** Core shared capabilities (multi-provider, tools, context, vision) + key/user context abstraction work. **Estimated effort:** 2–4 days (lighter scope since full local chat history moves to WASM phase).

**Reuse (strong here):** All current code is the starting point and "donor" for sharing: `ProviderCatalog.cs`, `AiProviderService.cs` (and `CreateOpenAICompatibleClient`), `ConversationService.cs` + `StreamMessageAsync` (ME.AI + tools loop, using server history), `DefaultToolProvider`/`AppTools.cs`, `ProviderKeyService`, `UserProviderKey`, `ChatfishDbContext`, Razor components (`Chat.razor`, `Settings.razor`, `MainLayout.razor`, `NavMenu.razor`), JS interop patterns, Markdig usage, CSS, `Roadmap.razor` md loader. The server target proves the core flows (with server chats).

**Challenges:** Current services and pages assume server DB + HttpContext for *everything* (biggest gap vs. documented local-first vision for *other* targets); "cross-device sync" story here relies on login + server storage (the richer local-only sync lives in targets 2/3); keeping the existing hosted UX unbroken. Full local chat history (the privacy win) is not the focus of this target.

---

## 2. Blazor WebAssembly Development (Browser-Native / Pure Client Target)

**Current State:** Preparation and intent only (visible in the roadmap and some csproj/Program.cs wiring). The `Microsoft.AspNetCore.Components.WebAssembly.Server` package + `AddInteractiveWebAssemblyComponents` + debug support exist, but `Routes.razor` globally forces `@rendermode InteractiveServer`, only server render mode is mapped in `Program.cs`, there is no Client/WASM project or shared RCL yet, and there is zero client-side persistence (no LocalStorage usage for chats/keys at all — everything goes through server SQLite via services). AI is always server-proxied. JS interop exists only for UX helpers (resize/scroll in MainLayout + Chat). This is the gap between the "privacy-first, LocalStorage by default" vision and today's server-centric reality.

**Note (July 2026):** A safe parallel WASM implementation was started in a separate ChatfishApp.Client project (modeled after initial template in bak). Core chat functionality (UI, local multi-convo history in browser storage, model selector, Ollama direct AI calls with streaming) is being built in new files (e.g. WasmChat.razor) **without modifying the server Chat.razor**. This allows testing local-first while keeping the server path deployable as fallback. See worktree for the Client project.

**Goal:** Move toward (and deliver) the true local-first architecture in a pure browser client. Conversations live encrypted in the browser only. Sync only among the user's local clients/browsers/profiles — the central server never sees or stores chat history. Local models (Ollama) are direct and first-class. Reuse as much of the existing Blazor investment as possible. Minimal (or optional thin-proxy) server involvement for the chat experience itself.

**Key Tasks**
- [Primary home for old Phase 4] Refactor Chat & History to Blazor WebAssembly + Encrypted LocalStorage + Cross-Device Sync (Future). (Keep similar scope as the prior Phase 4 description, but now it comes after multi-provider and context features are solid in Core. Expand with concrete steps from current code reality:)
  - Project structure evolution: Extract reusable pieces (UI components, layouts, CSS, `ProviderCatalog`, tool definitions, AI abstractions after they are interface-based) into a Razor Class Library (RCL) or shared project so they can be used by the server host (target 1), WASM host, *and* the MAUI hybrid (target 3). Add WASM hosting (new client project or modern single-project WASM patterns). Update the solution (`.sln`).
  - Enable real client rendering: Map `AddInteractiveWebAssemblyRenderMode()` (or Auto), update `Routes.razor` / `App.razor` (remove or condition the global server force), handle prerendering / `PersistentComponentState` where helpful. Support both server and WASM hosts during transition.
  - Client auth / identity: Fully abstract `IHttpContextAccessor` / server claims into `IUserContext` (populated from JWT / short-lived token or local anonymous mode). Magic-link flow can remain (thin server endpoint issues a token the WASM client then uses); pure-local no-login mode also supported.
  - Encrypted local persistence for history + keys: Implement `IChatHistoryStore` (and key store) using `ProtectedBrowserStorage`, or custom encryption + IndexedDB / LocalStorage / OPFS. History never leaves the browser. Keys client-side (privacy win; document the risk vs. the current server-stored option in target 1).
  - AI / tool calling from the client: Direct `IChatClient` calls where possible. Ollama: trivial direct fetch to `http://localhost:11434/v1` (user may need `--disable-web-security` or a tiny local helper for CORS during dev; production users run Ollama with proper CORS or the app on the same origin). Cloud providers: direct when CORS allows, or thin proxy for key/attribution header injection. Fully reuse the catalog, ME.AI abstractions, `UseFunctionInvocation`, and the existing free tools (web_search / Jina still work from browser).
  - Real streaming: Use the already-referenced `Microsoft.AspNetCore.SignalR.Client` package (or provider streaming + client handling) instead of the current fake chunked yields.
  - Offline + PWA: Make the WASM app installable, work offline for local models + existing history, cache static assets.
  - Sync chat history *only to local clients* (never on server): Concrete user-controlled mechanisms that do not involve the central chatfish server storing content:
    - Easy export/import of (optionally encrypted) JSON history bundles (per-convo or full).
    - "Sync folder" support: point the app at a user-owned location (local folder synced by Syncthing / Dropbox / Google Drive / NAS / WebDAV that *the user* controls) and read/write history files there.
    - Local network sync: when two instances are on the same LAN/WiFi, discover and offer peer-to-peer merge (user approves).
    - Future: file-based or CRDT-based merge for seamless multi-client.
    - Hard rule: the chatfish server (if used at all for auth or a thin proxy) receives *zero* conversation history or message content.
  - **Live authenticated sync (server as signaling/auth only — Brave model, real-time only when both devices open)**: In addition to the user-controlled local mechanisms above, support *live* cross-browser/device history sync for users who have logged in with the same email (magic link). This is real-time only (both WASM instances must be open and online at the same time) — no store-and-forward or persistent central copy of history. The server acts purely as authentication + signaling (like a WebRTC signaling server). It never stores or sees the chat content blobs.
    - How it works (high level): Both devices authenticate (cookie or thin token from the login flow). Server confirms both are online for that user and facilitates handshake. Devices then transfer the (encrypted) history snapshot or deltas. Server is "blind" to the payload.
    - Transfer options (key decision):
      - WebRTC Data Channels (preferred for true P2P — server only does signaling/auth; data never touches the server at all).
      - WebSocket relay via the server (simpler fallback; data passes through but is encrypted with a client-held key so server cannot read it).
    - Encryption: A per-user encryption key (random secret) is stored in the `User` table in the DB (protected at rest with IDataProtector). Authenticated WASM clients fetch the key over the secure channel. They use it to encrypt localStorage/IndexedDB blobs *and* the data transferred during live sync. This matches the requirement "besides email in the database, some kind of encryption key should be stored there."
    - Storage backend (important): `localStorage` has a ~5 MB limit. Use `IndexedDB` (via Blazor's `IJSObjectReference` or a wrapper) for the actual encrypted history blobs (much larger quota, async, good for structured data). Keep only small index/metadata in `localStorage` if desired. Current WasmConversationStore / WasmKeyStore use `localStorage` + plain JSON; this work will migrate the blobs to encrypted IndexedDB entries.
    - Sync flow (rough):
      Device A                  Server                  Device B
         |                         |                        |
         |-- Auth + "I'm online" ->|                        |
         |                         |<-- Auth + "I'm online"-|
         |                         |                        |
         |<-- "Device B is ready" -|                        |
         |                         |                        |
         |<======= WebRTC / WebSocket data channel ========>|
         |         (encrypted blob, server blind)           |
    - Key open design decisions (to be resolved during implementation):
      - Conflict resolution: If both devices modified data since last sync, which wins? Last-write-wins? Vector clocks + merge? User prompt?
      - Encryption in transit: Encrypt the blob client-side before sending (recommended, even if using WebRTC which is already over DTLS). Still do it beyond TLS.
      - Sync triggers: Automatic when peer comes online (server push / presence), periodic poll, or explicit "Sync now" button in the WASM UI?
      - Mid-sync disconnect: Partial transfer recovery, resume, or full re-sync on next connection?
      - WebRTC vs. relay default: WebRTC is the purest "server never sees the blob"; WebSocket relay is easier to implement first.
      - Storage: Prioritize IndexedDB for blobs; decide on schema (one object store per convo? encrypted index?).
    - This enables the "logged in (email) will be able to sync conversation history to another browser or device" vision while obeying "the history is never stored in the SQLite database" and "sync ... can only happen when they are both open."
    - The encryption key + email are the only things the WASM "gets from the database" for this feature (plus optional import of the user's server-side provider keys for convenience).
  - Update *all* affected UI and services: Chat.razor (render mode, local store instead of service for history), NavMenu (local convo list), Settings, Roadmap.razor (robust md loading that works from WASM host too), any other pages. Remove or conditionalize server-only assumptions.
  - Add Ollama as a first-class, zero-config experience (auto-detect, prominent in selector, "local" indicators).
  - Bring core context management, tools, and later vision/multimodal over the local store.

**Dependencies:** Core shared (multi-provider incl. Ollama work, context, tools, vision) + the abstraction + server-decoupling work from target 1 (so the WASM impl can be a clean alternative store rather than a second full rewrite). **Estimated effort:** 6–10 days.

**Reuse (excellent after extraction):** `ProviderCatalog.cs` (source of truth, just add Ollama), `AiProviderService` patterns (client-friendly overloads), the tool system (`DefaultToolProvider`, AppTools — web tools work great from browser), markdown + Markdig, chat UI/selector/streaming presentation, CSS, layout, JS interop (expand it), `Roadmap.razor` loader. After RCL extraction, the same components power targets 1 (hosted) and 3 (MAUI).

**Challenges (from current code exploration):** Heavy reliance on `HttpContext` and server DB throughout services and pages (biggest lift); auth surface (cookies don't just work; need token story); browser CORS + exposing cloud keys client-side for some providers; WASM payload size and cold-start; implementing robust client-side encrypted storage + streaming without the server crutch; coordinating so the existing hosted target 1 doesn't regress; no current client project or shared layer.

---

## 3. .NET MAUI with Blazor Web App (Hybrid Native Target) [NEW]

**Current State:** Not started. This is the new direction the user is very excited about after research. No MAUI projects, no BlazorWebView, no platform code, no native storage or WebView bridges yet. The Blazor components, catalog, tools, and AI wiring are all in the single server-centric project today.

**Goal:** Native desktop (Windows via WinUI/WebView2, Mac) and mobile apps using .NET MAUI + Blazor Hybrid. Deliver the best possible local + agentic experience: local AI (Ollama) as the effortless default, everything stored locally on the user's devices only, full reuse of the existing Blazor UI/components/services (via shared projects), and — the standout capability — rich, live AI visibility into (and agentic control over) browser tabs by embedding a real WebView control. Chat history syncs only between the user's local clients; the central server never sees or stores it.

**Key Benefits (from user research)**
- Reuse the existing components (Chat.razor UI, MainLayout + NavMenu, Settings, Roadmap renderer, CSS, model selector, streaming, markdown, services after abstraction) with only thin platform adapters.
- More local and native abilities (direct filesystem for exports/attachments, SecureStorage, camera for vision, notifications, true offline, excellent LAN reach for Ollama, background tasks).
- Use the WebView control to add web browsing with Agentic control + AI visibility into browser tabs (the killer feature that pure web or server targets can't match as cleanly or privately).

**Key Tasks (new + mapped from prior roadmap)**

- Solution and project evolution (maps to old Phase 4/5 structure needs but for native):
  - Create shared projects for maximum reuse: e.g. a Razor Class Library (`ChatfishApp.Shared` or similar) containing the Blazor components, `Contracts/ProviderCatalog`, abstracted services, CSS, etc. The existing `ChatfishApp` becomes (or stays) the server host for target 1. Add a WASM host for target 2. Add one or more new .NET MAUI Blazor Hybrid project(s) for target 3 (start with Windows desktop focus for quick value). Update `ChatfishApp.sln` and solution folders. Use multi-targeting or source sharing where helpful.
  - In the MAUI project: Standard MAUI + Blazor Hybrid setup (Microsoft.Maui.Controls + BlazorWebView). Main page hosts `<BlazorWebView HostPage="wwwroot/index.html" ...>` and loads the shared root component. Handle MAUI lifecycle, navigation, deep links if useful.

- Platform-native storage & strict privacy:
  - Implement the shared `IChatHistoryStore` / `IKeyStore` using MAUI `SecureStorage` (keys), `Preferences`, and/or device-local SQLite (via Microsoft.Data.Sqlite or EF Core with proper path). Richer history can live in a local DB file the user can back up.
  - **Hard rule:** All chat history, messages, and titles stay on the user's device(s). The central chatfish server (if the app even contacts it for auth or optional thin proxy) receives and stores *zero* conversation content. This is the realization of "syncing of chat history only to local clients (not saved on the server)".

- Local AI integration (Ollama) as the default, delightful experience:
  - Direct HTTP from the MAUI process to `http://localhost:11434` (or user-configured LAN address). No CORS headaches. Auto-detect models (`/api/tags`).
  - Prominent "Local (Ollama)" section or default in the model selector. "No key required" UX.
  - Cloud providers still fully supported (keys in SecureStorage).
  - This pairs perfectly with the powerful local tools and browser context (fast, private, free, works offline for the model if the model is local).

- **AI visibility into browser tabs + Agentic web browsing control (the exciting new capability; primary realization of old Phase 5 "browser awareness" + "MCP client + browser extension for tab awareness", made far more powerful and integrated):**
  - Build a `IBrowserContext` / platform bridge (injected into the shared tool provider or as additional context for the chat).
  - From the `BlazorWebView` (or the underlying platform WebView / WebView2), expose live state of the embedded browser: current URL, title, clean text content or HTML of the page, list of open tabs (if the hybrid shell supports multi-tab web browsing), etc.
  - Register rich, target-specific tools (using the existing `IToolProvider` + `AIFunctionFactory` / ME.AI pattern so any tool-calling model — local Ollama or cloud — can call them autonomously):
    - `list_browser_tabs()` → returns titles, URLs, ids.
    - `get_current_tab_content()` / `get_tab_content(tabId)` → returns LLM-friendly cleaned text (or structured extract).
    - `get_tab_html(tabId?)` (for models that want raw).
    - `navigate_to(url)`, `go_back()`, `reload()`, `search_in_page(query)` or limited safe `execute_js_in_tab` (with user-visible confirmation for mutating actions).
  - The model can now do things like: "Look at the tabs I have open and summarize the one about pricing", "Help me research this by browsing the documentation page I just loaded", "What's the current dashboard showing?", "Navigate to the login page and describe the form fields" (with safeguards), etc. This is *agentic browser control* + visibility, not just passive search.
  - UI integration: In the MAUI shell or chat header, show "Browser awareness: active" (with the current page title/URL) and a toggle to grant/revoke live page access to the AI. Traces of tool use ("Model used get_current_tab_content") can surface in the chat.
  - This is dramatically more capable and private than the current server-side web_search + Jina, works beautifully with local models, and is a unique advantage of the MAUI hybrid target.

- Sync chat history only to local clients (no central server storage):
  - Built-in or guided support for user-controlled mechanisms:
    - One-click export / import of history (encrypted bundles).
    - "Sync location": let the user point the app at a folder on their NAS, personal cloud drive (Dropbox etc. that they own), WebDAV, or Syncthing-synced dir; the app reads/writes history files there and merges on change.
    - Local network peer sync: discover other running instances of the MAUI app on the same LAN and offer secure merge (user confirms).
    - Future: conflict-free file-based or CRDT sync.
  - Explicit in docs and UI: "Your chats are only on your devices. The chatfish server (if used) never receives or stores them."

- Reuse strategy + shared code:
  - The goal is 80-90%+ code reuse. Shared RCL for all the Blazor UI (Chat, Settings, NavMenu, Roadmap, etc.), catalog, core services (after abstraction), markdown, CSS, tool definitions.
  - MAUI-specific: partial classes or DI for `IBrowserContext` (the real WebView impl), native storage, file pickers, camera (for vision), app chrome.
  - The same chat flow, model selector, streaming UI, agentic tool loop, etc. "just work" inside the WebView-hosted Blazor.

- Native + multimodal + advanced extras:
  - Drag-and-drop + platform file pickers for images/PDFs (vision).
  - Device camera access for "describe what I'm pointing the camera at".
  - Better handling of very large context (local model + local storage).
  - Notifications, shortcuts, background refresh if useful, proper offline design.
  - MCP servers: much easier to run or bundle useful local MCP tools on the user's machine.

- Polish: Consistent theming, good performance in hybrid (BlazorWebView has some overhead), handling WebView JS interop + .NET ↔ JS bridges for the browser tools (with proper threading), icons/splash, distribution (MSIX, pkg, etc.), graceful degradation if WebView context not granted.

**Dependencies:** Core shared capabilities + the abstraction layer (IUserContext, IChatHistoryStore, IKeyStore, IBrowserContext, IToolProvider extensions) + learnings + shared RCL from the WASM target work (so reuse is clean and we don't duplicate effort). The server target 1 provides the initial "it works" proof. **Estimated effort:** 8–15+ days (project scaffolding + RCL extraction + the WebView context bridge + new browser tools + sync UX are the big new pieces; the rest is reuse + adaptation).

**Why here / why exciting?** Reuses the Blazor components and AI/tooling investment already made. Unlocks truly local + native + the unique agentic browser visibility and control that server or pure-WASM targets can't deliver as powerfully or privately. Pairs perfectly with local Ollama (fast, private, zero marginal cost) and the "history never on the server" rule. This is the target that makes the "AI visibility into browser tabs" vision real and delightful.

**Reuse (the whole point of this target):** After extraction, the same `Chat.razor` (with its model selector, streaming, markdown), `MainLayout`/`NavMenu`, `Settings`, `Roadmap.razor`, `ProviderCatalog`, tool system, AI service patterns, etc. power the MAUI app with almost no duplication. Only platform adapters and the new browser-context tools are MAUI-specific.

**Challenges:** New project type and MAUI + Blazor Hybrid learning curve + platform differences (Windows WebView2 is excellent; Mac/iOS/Android vary); reliable, low-latency JS interop from Blazor to the live WebView content; packaging and distributing a hybrid app; keeping the shared code truly portable across server/WASM/MAUI; threading / lifecycle differences in hybrid; user education on granting browser access safely.

---

## Implementation Strategy & Cross-Cutting Work

- **Abstractions first (the key that unlocks everything):** Before heavy target-specific work, introduce `IUserContext` (replaces direct HttpContext/claims everywhere), `IChatHistoryStore` (client-local default impl for WASM/MAUI targets; the pure server target keeps server DB for chats), `IKeyStore`, `IBrowserContext` (stub/no-op in web targets, full WebView impl in MAUI), and make `IToolProvider` / tool registration easy to extend with target-specific tools. Refactor the existing services and pages to the interfaces. This makes adding WASM and MAUI clean instead of forks. (The server target may keep its current server chat persistence for hosted convenience.)
- Recommended sequence (so hosted value continues while we build the future):
  1. Core multi-provider work (including groundwork for local models like Ollama) + server target 1 updates (abstractions for user context and keys; keep server-side chat history for hosted account convenience).
  2. Extract shared RCL + implement the pure WASM target (part 2) with local storage, direct calls, and local-only sync. This is the primary phase for full local chat history (encrypted LocalStorage / device storage, never on server). Full Ollama support (auto-discovery, seamless localhost experience) lands here and in target 3.
  3. Add the MAUI hybrid target (part 3) + the WebView browser context bridge and tools (this is where "AI visibility into browser tabs" + agentic control become real and powerful). Ollama is a first-class, zero-config default in the native app; local chat history on device.
- Old Phase 5 "browser extension for tab awareness" idea is deprioritized (or kept as a lighter optional for pure-web users) in favor of the superior, integrated MAUI WebView realization. MCP concepts can apply across targets (device-local servers are easiest in 3).
- The "local clients only, never saved on the server" rule (full local chat history) is designed into parts 2 and 3 from the start. The pure server hosted target (part 1) uses server-side chat storage tied to logins for convenience and cross-browser access.
- Each target owns its render mode, storage story, and any unique tools (browser tabs = 3 only for now). Shared code stays as portable as possible.
- Docs: This `ROADMAP.md` remains the single source (rendered by the web targets' `/roadmap`; MAUI app includes it too via the shared renderer or a native page). Possibly add target-specific "getting started" notes later.
- ...

## Phase Remapping Notes (for continuity)

- Old Phases 1, 2, 3, 6 → moved into **Core Shared Capabilities** (full task lists preserved, delivered items stay strikethrough-marked, added notes on target differences for storage, Ollama ease, tool surface, vision capture).
- Old Phase 4 (WASM + Encrypted LocalStorage + Cross-Device Sync) → primary detailed home in **part 2**, with clear applicability and reuse notes for part 3.
- Old Phase 5 (MCP Client + Browser Awareness + MCP Servers) → high-level concepts in Core; the concrete, high-value implementation of browser tab awareness + agentic control lives in **part 3** (WebView). Lighter versions or extension ideas can still be noted for web targets.
- All prior "Future extensions", "Why here?", efforts, and dependencies are carried forward under the appropriate new headings.
- Delivered work (OpenRouter, Gemini, tool wiring, etc.) remains clearly marked as delivered in the Core section.

**Last updated:** July 2026 (Restructured around the three parts; removed Ollama from pure Interactive Server target 1; clarified that full local chat history (never on server, LocalStorage/device storage) is delivered in the WASM phase (target 2) and MAUI (target 3), while the hosted server target keeps server-side chats for account convenience. All prior phase details correctly placed; local-only history and browser visibility emphasized for the client targets.)

**Maintained in:** `ROADMAP.md` (the source of truth) + `Pages/Roadmap.razor` (for web targets 1 and 2); the MAUI app (target 3) will include the roadmap content (via the shared renderer or a dedicated page).

**Notes:**
- Grok / xAI OAuth integration has been deprioritized for now. It may be revisited later once the product is more mature and after requesting an official client_id from xAI.
- Focus is on reliable, useful multi-model support + excellent context awareness + building out the three delivery targets on a shared, local-first foundation.
- The three parts let us deliver incremental value (the current server app keeps working and improving) while investing in the higher-privacy, higher-capability local and native experiences.

(End of roadmap.)
