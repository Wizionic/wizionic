# Chatfish.me Project Roadmap

**Version:** 1.0 (May 31, 2026)  
**Developer:** Daniel Goodwin — Senior Web Application Developer (25+ years experience)  
**Project Status:** Active personal / resume project

## Project Vision & Goals
Chatfish.me is a **privacy-first, local-first AI chat hub** that lets you chat with multiple AI models (starting with Groq and Grok via OAuth) while keeping conversations in the browser.  
It will eventually support cross-device sync, MCP tools, and browser tab awareness — all while keeping server resource usage minimal.

Primary goals:
- Strong recent AI development experience for resume
- Genuinely useful tool for myself, family, and eventually the public
- Privacy-first (conversations stay in browser LocalStorage by default)
- Minimal server load (cheap hosting friendly)
- Built with modern Blazor best practices

---

## Phase 1: Grok OAuth Integration (Highest Priority)
**Goal:** Use my existing SuperGrok subscription directly — no separate API key needed for Grok.

**Key Tasks**
- Implement xAI OAuth 2.0 login flow
- Secure token storage (LocalStorage + refresh logic)
- Add Grok as a first-class provider alongside Groq
- UI toggle to enable/disable Grok model
- Handle token expiration and silent refresh

**Why first?** Immediate personal value + strong resume signal.

**Dependencies:** None  
**Estimated effort:** 1–2 days

---

## Phase 2: Refactor Chat & History to Blazor WebAssembly + Encrypted LocalStorage + Cross-Device Sync
**Goal:** Move chat UI, history, and storage to WASM for true local-first behavior.

**Key Tasks**
- Convert current InteractiveServer chat components to InteractiveWebAssembly (or hybrid Auto render mode)
- Implement encrypted LocalStorage / IndexedDB for all conversations
- Add user account system (minimal) for sync
- Build encrypted sync mechanism (upload/download encrypted conversation blobs)
- Add “Sync this conversation” / “Enable auto-sync” toggles
- PWA support

**Dependencies:** Phase 1  
**Estimated effort:** 4–6 days

---

## Phase 3: MCP Client (Model Context Protocol)
**Goal:** Make Chatfish MCP-compatible so any connected AI model can discover and use external tools.

**Key Tasks**
- Add MCP client library (JSON-RPC over HTTP/WebSocket)
- UI for discovering / connecting to MCP servers
- Permission management per server
- Integrate with Groq + Grok model calls
- Store enabled MCP servers in LocalStorage

**Dependencies:** Phase 2  
**Estimated effort:** 3–5 days

---

## Phase 4: Browser Plugin / Extension for Tab Awareness
**Goal:** Let the AI “see” your open browser tabs.

**Key Tasks**
- Build companion browser extension (Chrome/Edge/Firefox)
- Extension ↔ Blazor app messaging
- Expose list of open tabs + page content
- Create MCP server inside the extension for tab context

**Dependencies:** Phase 2 + 3  
**Estimated effort:** 5–8 days

---

## Phase 5: MCP Server(s)
**Goal:** Provide useful Chatfish-specific tools.

**Key Ideas**
- Personal Knowledge Base server
- File system / attachment server
- Family sharing server
- Browser tab context server (powered by the extension)

**Dependencies:** Phase 3 + 4  
**Estimated effort:** 3–6 days

---

**Last updated:** May 31, 2026  
**Maintained in:** `ROADMAP.md` + `Pages/Roadmap.razor`
