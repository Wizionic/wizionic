# Chatfish.me Project Roadmap

**Version:** 2.0 (June 2026)  
**Developer:** Daniel Goodwin — Senior Web Application Developer (25+ years experience)  
**Project Status:** Active personal / resume project

## Project Vision & Goals

Chatfish.me is a **privacy-first, local-first AI chat hub** that lets you chat with multiple AI models while keeping conversations in the browser. It supports local models (Ollama), cloud providers via API keys (OpenRouter, Gemini, generic OpenAI-compatible), and will eventually add advanced features like cross-device sync, MCP tools, and browser awareness — all while keeping server resource usage minimal.

Primary goals:
- Strong recent multi-LLM and AI tooling development experience for resume
- Genuinely useful tool for myself, family, and eventually the public
- Privacy-first (conversations stay in browser LocalStorage by default)
- Minimal server load (cheap hosting friendly)
- Built with modern Blazor best practices
- Excellent context management and user awareness of token usage

---

## Phase 1: Multi-Provider Support (Highest Priority)

**Goal:** Allow users to easily chat with many different models without being locked into one provider.

**Key Tasks**
- Add Ollama support (local models, auto-detect available models)
- ~~Add OpenRouter integration (via user-provided API key)~~ — delivered (see OpenRouter provider + attribution headers + many models including tool-calling ones)
- ~~Add Google Gemini Flash 2.0 support~~ — delivered (gemini-2.5-flash via compat; notes on new projects + free tier without billing)
- Add generic OpenAI-compatible provider (base URL + API key) so users can connect to any compatible endpoint (Groq already exists, add others)
- Clean model selector UI that groups models by provider
- Per-provider connection status and API key management (secure storage)

**Why first?** Immediate value and flexibility. Users can start using powerful free/local models right away.

**Dependencies:** None  
**Estimated effort:** 4–7 days

---

## Phase 2: Context Length Management & Intelligence

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

**Why important?** One of the biggest pain points with long chats is hitting the context wall unexpectedly. This feature will be very user-friendly and impressive on a resume.

**Dependencies:** Phase 1 (need model metadata)  
**Estimated effort:** 5–8 days

---

## Phase 3: Tool Use, Web Search & Agentic Behavior (incl. Jina URL Summarization)

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

**Why here?** This is the natural evolution after multi-provider (Phase 1). It directly delivers "the model can search the web etc. when it needs to." It also makes the original Jina Phase 3 far more powerful (model decides to use the summarizer). Ties beautifully into later MCP / browser awareness phases (more tools the model can autonomously invoke). Keeps the "completely free models" spirit by using free backends (DDG + Jina free tier).

**Dependencies:** Phase 1 (pluggable IChatClient + per-user keys + OpenRouter for great tool-calling model selection)  
**Estimated effort:** 3–6 days (core wiring + 2 tools + UI hints + docs)

**Future extensions in this area**
- More tools (user files, code interpreter sandbox if safe, calendar, etc.).
- Surface tool traces in chat history / "thinking" steps.
- Real streaming + live tool events (instead of post-full-response fake stream).
- Advanced agent patterns (memory, planning, multi-step workflows, handoffs) — .NET equivalent of what OpenRouter's Agent SDK provides for TS/Python.
- Per-convo "agent mode" toggle or budget/limits for tool usage.
- Integration with Phase 5 MCP client (models can discover and call MCP tools).

---

## Phase 4: Refactor Chat & History to Blazor WebAssembly + Encrypted LocalStorage + Cross-Device Sync (Future)

**Goal:** Move toward true local-first architecture.

(Keep similar scope as before, but now it comes after multi-provider and context features are solid.)

**Dependencies:** Phase 1–3  
**Estimated effort:** 6–10 days

---

## Phase 5: MCP Client + Browser Awareness + MCP Servers (Future)

Keep the original later phases (MCP client, browser extension for tab awareness, useful MCP servers).

**Dependencies:** Phase 4  
**Estimated effort:** 8–15+ days

---

## Phase 6: Multimodal / Vision Support (Future)

**Goal:** Allow users to upload, paste, or drag images (and eventually documents/PDFs) so that vision-capable models can "see" them and answer questions about the content.

**Key Tasks**
- Add image upload / paste / drag-and-drop support in the chat input area (preview thumbnails, remove button).
- Extend the message model / history to carry image attachments (store as base64 or local file refs for now; later encrypted localstorage in WASM phase).
- Add `SupportsVision` flag to `ModelDefinition` in the catalog (mark models like GPT-4o, Gemini 2.x, Claude 3+, Llama 3.2 vision, etc. on OpenRouter).
- When building `ChatMessage` list for a vision model, include `ImageContent` (or DataContent) using Microsoft.Extensions.AI types.
- Only show the image upload UI (or enable it) when the currently selected model supports vision.
- Handle provider-specific details (some use base64 data URLs, some need special content parts).
- Graceful fallback / error if a non-vision model is chosen with images attached.
- Optional: basic document support (e.g. extract text from PDF/images via OCR or simple libs for text PDFs).

**Why here?** Many modern models on OpenRouter and elsewhere are multimodal. This is a high-value, resume-friendly feature that builds directly on the multi-provider + tool foundation. It enables use cases like "describe this screenshot", "analyze this chart", "read this whiteboard photo", "summarize this PDF page", etc. Fits nicely before or alongside full MCP/browser awareness.

**Dependencies:** Phase 1 (pluggable providers + catalog to mark vision models), good error handling for unsupported cases.
**Estimated effort:** 4–8 days (UI upload + storage + ME.AI content parts + catalog flag + provider quirks).

---

**Last updated:** June 2026 (OpenRouter + conditional tools + provider enable/disable + model labels + vision roadmap item)  
**Maintained in:** `ROADMAP.md` + `Pages/Roadmap.razor`

---

**Last updated:** June 2026 (OpenRouter + tool calling / web search delivered)  
**Maintained in:** `ROADMAP.md` + `Pages/Roadmap.razor`

**Notes:**
- Grok / xAI OAuth integration has been deprioritized for now. It may be revisited later once the product is more mature and after requesting an official client_id from xAI.
- Focus is on reliable, useful multi-model support + excellent context awareness first.
