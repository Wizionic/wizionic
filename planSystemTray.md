# Evaluation: Multi-step DAGs & OS background (workflows / tools / sync)

This is an architecture evaluation only — no implementation recommended as immediate work unless you choose a tier below.

---

## 1) What are multi-step DAGs?

**DAG** = Directed Acyclic Graph: a graph of steps with arrows “A must finish before B,” no loops that would run forever.

Today Wizionic workflows are **one-shot**:

```
trigger (cron/once/manual) → pick model → execute_skill (single skill) → log
```

A **multi-step DAG** would allow sequences and forks, for example:

```
                    ┌─ skill: research-url ─┐
cron 9am ──► start ─┤                       ├── skill: write-note ──► end
                    └─ skill: stock-check ──┘
```

Typical capabilities people mean by “multi-step workflows”:

| Capability | What it means | Wizionic today |
|------------|---------------|----------------|
| **Sequence** | Step 2 uses output of step 1 | No — one `execute_skill` |
| **Parallel** | Two skills at once, then join | No |
| **Branching** | If success / if stock down → different skill | No |
| **Data flow** | Pass `result` JSON between steps | No (skill body only) |
| **Retries / timeouts** | Per-step policy | No |
| **Human-in-the-loop** | Pause for approval | No |

**Not the same as OWS:** CNCF Open Workflow Spec is a full service-orchestration DSL. Multi-step DAGs for Wizionic would more likely stay custom YAML (`steps: [...]`) reusing `SkillRunner`, not a full OWS engine.

**When you’d want them:** “Every morning: search news → summarize → note + optional HA light.” Today you’d encode that **inside one skill’s instructions** and hope the model does multiple tool calls. Multi-step DAGs make that **deterministic** (run skill A, then skill B) instead of model-dependent.

**Rough effort if ever built:** medium–large (schema + orchestrator state machine + UI for steps + failure handling). Independent of background execution. **Recommend defer** until single-skill workflows feel solid.

---

## 2) OS background — what’s realistic?

### 2.1 Separate processes (already true)

| Piece | Runs when MAUI closed? | Why |
|-------|------------------------|-----|
| **Homeserver** | Yes, if installed as Windows Service / systemd / user session service | Own process; `UseWindowsService` host |
| **Lemonade** | Yes, if installed as its service | Own server process |
| **Ollama** | Yes, if user/service keeps it running | Own process |

These only provide **HTTP APIs** (auth, proxy chat, models). They do **not** run your `WorkflowOrchestrator`, skills library, or WebRTC sync by themselves.

### 2.2 Where workflows actually live today

```
AppLayout (Blazor)
  └── WorkflowDueBootstrap  (~1 min timer while UI circuit alive)
        └── IWorkflowOrchestrator
              └── ISkillRunner
                    └── IChatCompletionService + IToolModule[]
                          ├── Native → HTTP to homeserver /api/tools/*
                          ├── Notes / Gallery / Calendar → local SQLite/stores
                          ├── Lemonade → local HTTP
                          ├── Browser / HA → MAUI-only services (WebView / HA API)
                          └── MCP / OAuth → KeyStore + network
```

So your instinct is right: **workflows are not “on the homeserver.”** They are **in-process** with MAUI (or WASM tab). The due ticker is even mounted as a **Blazor component** (`WorkflowDueBootstrap`), not a platform `BackgroundService`.

### 2.3 “Tightly coupled to UI?” — nuanced

| Tool area | Needs visible UI / WebView? | Needs MAUI (or WASM) **process** + DI + unlocked keys? |
|-----------|----------------------------|--------------------------------------------------------|
| Native (search, weather, time, calc, summarize) | No | Yes (client calls host APIs; keys/model pick) |
| Notes / Gallery / Calendar | No | Yes (local encrypted stores) |
| Lemonade image/STT/TTS | No | Yes (client → Lemonade URL) |
| MCP / OAuth connectors | No | Yes (tokens in KeyStore) |
| **Browser agent** | **Yes** (embedded WebView) | Yes |
| Home Assistant | No (HTTP) | Yes (MAUI module + stored URL/token) |
| **Sync (WebRTC)** | No UI | Yes (SignalR + peer + local stores) |

So: **most tools are process-coupled, not window-coupled.** Closing the *window* is different from killing the *process*.

### 2.4 Scenarios

| Scenario | Process alive? | Workflows fire? | Sync works? |
|----------|----------------|-----------------|-------------|
| MAUI focused | Yes | Yes (`WorkflowDueBootstrap`) | Yes |
| **Minimized** (taskbar) | Yes | **Yes** (same process; timer keeps running) | **Yes** |
| **System tray** (hide main window) | Yes | **Yes**, if we keep the host running | **Yes**, same |
| Locked screen / sleep | Yes but OS may throttle / suspend | Best-effort; sleep pauses timers | Best-effort |
| **Process fully exited** | No | **No** | **No** |
| WASM browser tab backgrounded | Tab may freeze timers | Unreliable | Unreliable |
| WASM tab closed | No | No | No |

**Assumption confirmed:** minimized MAUI should still run due workflows today, as long as Windows doesn’t suspend the app aggressively (desktop WinUI usually keeps running when minimized).

### 2.5 System tray option

**What it is:** User “closes” to tray → main window hidden, process + Blazor WebView + DI stay alive → same code path as minimized.

**Pros**
- Small conceptual change for users (“leave Wizionic running”)
- Workflows + sync keep working without redesign
- No need to reimplement tools on homeserver
- Aligns with local-first (keys and SQLite stay in user session)

**Cons / work**
- Windows-specific tray (and Linux tray if desired); iOS/Android different story
- Must handle: single-instance, exit vs hide, startup with Windows, update (Velopack) while tray-resident
- Power users still need “Quit” that actually stops workflows
- Does **not** help if user fully quits or reboots without auto-start

**Effort estimate:** **small–medium** (on order of days, not weeks) for a solid Windows tray + “Start minimized / Run at login” — mostly platform chrome, not new orchestration.

### 2.6 True background when MAUI is fully closed

You need a **second always-on host** that can:

1. Load preferences + skills + workflows (same stores / encryption)
2. Resolve models (Ollama/Lemonade/proxied)
3. Run `SkillRunner` without Blazor
4. Optionally keep SignalR + WebRTC for sync

Options:

| Approach | Feasibility | Effort | Notes |
|----------|-------------|--------|-------|
| **A. Tray / run at login (keep process)** | High | Small–medium | Recommended first step |
| **B. Headless MAUI / console worker** sharing code | Medium | Large | Duplicate lifecycle, key unlock, no Browser tools |
| **C. Windows Service “Wizionic Agent”** | Medium–hard | Large | Services often run as SYSTEM — DPAPI/user KeyStore/SQLite path pain; WebRTC harder |
| **D. Homeserver runs workflows** | Architecturally awkward | Very large | Breaks local-first: skills/history encrypted client-side; server shouldn’t hold chat content. Could only run **server-safe** tools (proxy chat + native HTTP) without notes/gallery/calendar bodies unless keys move server-side (undesirable) |
| **E. OS Task Scheduler** launches short worker | Medium | Medium–large | Good for cron; cold start + key access each fire |

**Sync when closed:** Same constraint. Homeserver can stay up for **signaling**, but **data sync is peer-to-peer WebRTC between devices that have the local stores**. A dead MAUI process cannot accept or send encrypted payloads. Tray keeps sync; full quit does not, unless you build a headless agent (B/C).

### 2.7 Verdict: is background “too much work”?

| Goal | Too much? | Recommendation |
|------|-----------|----------------|
| Workflows while **minimized** | Already works (or nearly) | Document + maybe small reliability (move ticker off Blazor to MAUI `IHostedService` / singleton timer so it doesn’t depend on circuit) |
| Workflows while **tray / “closed” window** | Not too much | **Best ROI** for desktop |
| Workflows + sync after **full quit** | **Yes, large** | Defer; only if product requires “agent always on” |
| Multi-step DAGs | Separate product feature | Defer until single-step + reliability proven |
| Homeserver-as-workflow-engine | **Yes + fights local-first** | Avoid for skill/tool runs that need local stores |

**Bottom line:** You are right that **UI-coupled tools (especially Browser)** and **in-process DI** make “quit MAUI but still run everything” hard. You are also right that **homeserver ≠ workflow runner**. The realistic path is:

1. Treat desktop Wizionic as a **long-lived agent process** (minimize + tray + optional run-at-login).
2. Keep due-run ticker process-level (not only Blazor).
3. Do **not** invent a second homeserver workflow runtime until/unless you accept a narrow subset of tools (HTTP-only, no local encrypted content).

---

## Optional future tiers (if you want to implement later)

### Tier 0 — Document behavior (trivial)
- Docs: “Scheduled workflows run only while the app process is running (including minimized).”

### Tier 1 — Reliability without tray (small)
- Move `ProcessDue` from `WorkflowDueBootstrap` to MAUI-registered timer / hosted loop so it survives Blazor circuit quirks.
- On app resume, immediate `ProcessDueAsync`.

### Tier 2 — Windows system tray + run at login (small–medium) **← best next product step if desired**
- Hide to tray on close; context menu Open / Quit.
- Optional “Start with Windows.”
- Sync + workflows continue while tray-resident.

### Tier 3 — Multi-step DAGs (medium–large, product)
- YAML `steps: [{ execute_skill }, …]` sequential first; parallel/branch later.

### Tier 4 — Headless agent / true quit-safe (large)
- Only if Tier 2 is insufficient.

---

## Decision summary for you

| Question | Answer |
|----------|--------|
| Multi-step DAGs? | Graph of skill steps with order/data flow; **not built**; skills can still multi-tool in one run via the model |
| Workflows when minimized? | **Yes** (process alive) |
| Workflows when tray? | **Yes** if we keep process (good investment) |
| Workflows when fully quit? | **No** today; hard + may conflict with local-first |
| Sync when quit? | **No** without a headless agent |
| Homeserver runs workflows? | **Not realistic** for notes/gallery/calendar/browser without redesign |

**No code changes in this plan.** If you want implementation next, the sensible pick is **Tier 1 and/or Tier 2 (tray)**, not DAGs or homeserver-side workflows.
