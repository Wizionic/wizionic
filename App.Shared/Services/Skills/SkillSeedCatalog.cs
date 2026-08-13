using App.Core.Skills;

namespace App.Shared.Services.Skills;

/// <summary>Built-in example skills (import on demand).</summary>
public static class SkillSeedCatalog
{
    public static IReadOnlyList<SkillRecord> CreateExamples()
    {
        return new[]
        {
            Make("positive-inspiration-image", PositiveInspirationMarkdown),
            Make("random-house-lights", RandomHouseLightsMarkdown),
            Make("stock-snapshot-note", StockSnapshotNoteMarkdown),
            Make("github-morning-brief", GitHubMorningBriefMarkdown),
            Make("github-create-issue-from-note", GitHubCreateIssueMarkdown),
            Make("weekly-planning-block", WeeklyPlanningBlockMarkdown),
            Make("research-url-to-notes", ResearchUrlToNotesMarkdown),
        };
    }

    private static SkillRecord Make(string name, string markdown) => new()
    {
        Id = name,
        Name = name,
        Markdown = markdown.Trim() + "\n",
        Enabled = true,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private const string PositiveInspirationMarkdown = """
---
name: positive-inspiration-image
description: >
  Generate a positive inspirational image with an uplifting message overlaid,
  save the image to the Positive pics gallery album, and save the message text
  to the Positive message notebook. Use when the user wants inspiration, positive
  vibes, uplifting images, or runs /positive-inspiration-image.
license: MIT
compatibility: Wizionic with Lemonade (or other image generation) + Gallery + Notes
metadata:
  author: wizionic
  version: "1.0"
  tags: gallery,notes,image,inspiration
  trigger-phrases: inspiration, positive vibes, uplifting image
allowed-tools: Lemonade Gallery Notes list_gallery_albums save_to_gallery list_recent_chat_images list_notebooks create_notebook add_note_entry
---

# Positive inspiration image

## Purpose
Create a short uplifting message, generate a matching inspirational image, save the image to Gallery album **Positive pics**, and save the message text to notebook **Positive message**.

## Steps
1. Invent a brief, original uplifting message (1–2 sentences). Keep it kind and non-religious unless the user asked otherwise.
2. Generate an image that matches the message (warm light, hopeful scenery or abstract positivity). Prefer Lemonade image tools when available; otherwise describe the image and use whatever image tools are available.
3. Ensure a gallery album named exactly `Positive pics` exists (create if needed via gallery tools).
4. Save the generated image into that album using `save_to_gallery` (use the latest chat image if listed by `list_recent_chat_images`).
5. Ensure a notebook named exactly `Positive message` exists (`list_notebooks` / `create_notebook`).
6. Add a note entry with the uplifting message text (`add_note_entry`).
7. Confirm to the user: album name, notebook name, and the message text.

## Examples
- User: "Give me a positive boost"
- User: /positive-inspiration-image

## Notes
- If Gallery or Notes tools fail, still share the message and image in chat.
- Do not skip saving when tools are available.
""";

    private const string RandomHouseLightsMarkdown = """
---
name: random-house-lights
description: >
  Set all lights in the house to a random color at about 40% brightness via Home Assistant.
  Use when the user wants party lights, random colors, colorful house, or runs /random-house-lights.
license: MIT
compatibility: Wizionic desktop (MAUI) with Home Assistant configured
metadata:
  author: wizionic
  version: "1.0"
  tags: home-assistant,lights
  trigger-phrases: party lights, random colors, colorful lights
allowed-tools: HomeAssistant ListEntities ListLights ControlLight CallService
---

# Random house lights

## Purpose
Turn house lights into a colorful scene: each light gets a random color at roughly 40% brightness.
Do **not** merely toggle lights off/on. Colors and ~40% brightness are required.

## Steps
1. Discover lights with Home Assistant tools (`ListLights` or `ListEntities` for domain light). Prefer `ListLights`.
2. For **each** light entity found (skip only if permanently unavailable if the tool cannot target it):
   - Turn the light **on** with a **random vivid color** (vary hue across lights — e.g. different RGB or named colors).
   - Set brightness to approximately **40%** (brightness ~102 on 0–255, or brightness_pct 40).
   - Prefer `ControlLight` with color + brightness parameters; otherwise `CallService` `light.turn_on` with `rgb_color` / `hs_color` and brightness.
3. Do **not** use Assist/`ProcessConversation` as the primary path — use ListLights + ControlLight/CallService.
4. If no lights are found, say so clearly and stop.
5. Summarize each light with the color you applied.

## Examples
- User: "Party mode lights"
- User: /random-house-lights

## Notes
- This skill is intended for MAUI with Home Assistant configured. If HA tools are unavailable, explain that.
- Prefer real tool calls over claiming success without calling tools.
""";

    private const string StockSnapshotNoteMarkdown = """
---
name: stock-snapshot-note
description: >
  Look up a stock ticker (default MSFT if none given) using the embedded browser when available,
  extract price and change, summarize, and save a dated entry to the Stock snapshots notebook.
  Use when the user wants a stock quote, market snapshot, or runs /stock-snapshot-note.
license: MIT
compatibility: Best on Wizionic MAUI with browser panel; falls back to search_web/summarize_url
metadata:
  author: wizionic
  version: "1.0"
  tags: browser,notes,stocks,finance
  trigger-phrases: stock quote, stock snapshot, ticker price
  input-schema: '{"ticker":{"type":"string","description":"Stock symbol e.g. MSFT","default":"MSFT"}}'
allowed-tools: BrowserAgent Native Notes navigate_to get_page_content search_web summarize_url get_time list_notebooks create_notebook add_note_entry
---

# Stock snapshot → note

## Purpose
Produce a short, factual stock snapshot for a ticker and **persist it** in notebook **Stock snapshots**.

## Steps
1. Determine the ticker symbol from the user message or run parameters. If missing, use **MSFT**.
2. Prefer the embedded browser when tools are available:
   - `navigate_to` a public quote page, e.g. `https://finance.yahoo.com/quote/{TICKER}`
   - `get_page_content` and extract last price, day change, and any clear volume/market-cap if present.
3. If browser tools fail or are unavailable, fall back to `search_web` for `{TICKER} stock price` and/or `summarize_url` on a public quote URL.
4. Call `get_time` (if available) for the snapshot timestamp.
5. Ensure notebook **Stock snapshots** exists (`list_notebooks` / `create_notebook`).
6. `add_note_entry` with a clear title and body, e.g.:
   - Ticker, price, change, source URL, and timestamp
   - 2–4 sentence plain-language summary (not investment advice)
7. Reply in chat with the same snapshot and confirm the note was saved.

## Examples
- /stock-snapshot-note
- /stock-snapshot-note AAPL
- "Snapshot NVDA into my notes"

## Notes
- Not financial advice. If data is missing or the page is blocked, say so honestly.
- Do not invent prices — only report what tools returned.
""";

    private const string GitHubMorningBriefMarkdown = """
---
name: github-morning-brief
description: >
  Using the connected GitHub OAuth connector, fetch the authenticated user, list recent repos,
  peek at open issues on the most relevant repo, and save a morning brief to notebook GitHub brief.
  Use when the user wants a GitHub status check or runs /github-morning-brief.
license: MIT
compatibility: Requires GitHub connected on Tools (OAuth connector)
metadata:
  author: wizionic
  version: "1.0"
  tags: github,oauth,notes
  trigger-phrases: github brief, github status, my repos
allowed-tools: MCP Notes github_get_user github_list_repos github_list_issues list_notebooks create_notebook add_note_entry
---

# GitHub morning brief

## Purpose
Give a concise “what’s going on in my GitHub” summary and save it to notes.

## Steps
1. Call `github_get_user` for the authenticated profile (login, name).
2. Call `github_list_repos` and pick up to **5** repos (prefer recently pushed / updated if the payload shows dates).
3. Optionally call `github_list_issues` on the top repo (owner/repo) for open issues — summarize count + top 3 titles only.
4. If any GitHub tool fails with auth errors, stop and tell the user to **Connect GitHub** on the Tools page.
5. Ensure notebook **GitHub brief** exists; `add_note_entry` with:
   - Date heading
   - User login
   - Bullet list of repos (name + one-line purpose if description present)
   - Issues peek (if fetched)
6. Reply in chat with the same brief (keep it short).

## Examples
- /github-morning-brief
- "GitHub status for me"

## Notes
- Read-only: do not create or close issues in this skill.
- Prefer real tool results; never invent repo or issue names.
""";

    private const string GitHubCreateIssueMarkdown = """
---
name: github-create-issue-from-note
description: >
  Create a GitHub issue in a repo the user specifies (owner/repo) using the GitHub OAuth connector,
  then log the issue URL in notebook GitHub issues. Use when filing a bug from chat or /github-create-issue-from-note.
license: MIT
compatibility: Requires GitHub connected on Tools with permission to create issues on the target repo
metadata:
  author: wizionic
  version: "1.0"
  tags: github,oauth,notes,issues
  trigger-phrases: create github issue, file a bug, open issue
  input-schema: '{"owner":{"type":"string"},"repo":{"type":"string"},"title":{"type":"string"},"body":{"type":"string"}}'
allowed-tools: MCP Notes github_create_issue github_get_user list_notebooks create_notebook add_note_entry
---

# Create GitHub issue + log note

## Purpose
Turn a natural-language bug report into a real GitHub issue, then record the link in Notes.

## Steps
1. Parse **owner**, **repo**, and issue **title/body** from the user message or run parameters.
   - If owner/repo missing, ask in chat (do not guess a private repo).
   - Title: short imperative summary. Body: steps to reproduce / expected / actual when possible.
2. Optionally `github_get_user` to confirm auth is working.
3. Call `github_create_issue` with owner, repo, title, and body.
4. From the tool result, extract the issue HTML URL or number.
5. Ensure notebook **GitHub issues** exists; `add_note_entry` with title, repo, URL, and a one-line summary.
6. Tell the user the issue URL clearly.

## Examples
- /github-create-issue-from-note owner=octocat repo=Hello-World title="Login button broken" body="..."
- "File an issue on myorg/myapp: crash when opening settings"

## Notes
- If the tool returns 401/403/404, explain permissions or wrong owner/repo — do not fake success.
- Do not mass-create issues; one issue per run unless the user explicitly asks for more.
""";

    private const string WeeklyPlanningBlockMarkdown = """
---
name: weekly-planning-block
description: >
  Using the current time, schedule three Focus Block events this week (e.g. Mon/Wed/Fri morning)
  on the default calendar and create a Weekly plan checklist note. Use for weekly planning or /weekly-planning-block.
license: MIT
compatibility: Wizionic with Calendar + Notes
metadata:
  author: wizionic
  version: "1.0"
  tags: calendar,notes,planning
  trigger-phrases: weekly plan, focus blocks, plan my week
allowed-tools: Calendar Notes Native get_time list_calendars list_events add_calendar_event list_notebooks create_notebook add_note_entry
---

# Weekly planning block

## Purpose
Lay down a light weekly structure: three **Focus block** calendar events + a planning note.

## Steps
1. Call `get_time` to establish “now” and the local date.
2. `list_calendars` and pick the primary/default calendar (first writable / default if unclear).
3. Choose three weekdays still remaining in the next 7 days (prefer Mon/Wed/Fri; if past, use next occurrences).
4. For each day, `add_calendar_event` titled **Focus block**, roughly **09:00–10:00** local (1 hour), with a short description “Deep work — protected time”.
5. Avoid exact duplicates: if `list_events` already shows a Focus block that day, skip creating another.
6. Ensure notebook **Weekly plan** exists; `add_note_entry` with a checklist:
   - Top 3 outcomes for the week
   - Links/reminders for the three focus blocks (dates/times)
7. Confirm in chat what was scheduled.

## Examples
- /weekly-planning-block
- "Set up my focus blocks this week"

## Notes
- Do not delete existing events.
- If calendar tools fail, still create the Weekly plan note with suggested times.
""";

    private const string ResearchUrlToNotesMarkdown = """
---
name: research-url-to-notes
description: >
  Open or fetch a URL from the user message, extract key points, and save a bullet summary plus the link
  into notebook Research. Prefer the embedded browser; fall back to summarize_url. Use for research or /research-url-to-notes.
license: MIT
compatibility: MAUI browser preferred; summarize_url works on WASM/host
metadata:
  author: wizionic
  version: "1.0"
  tags: browser,notes,research
  trigger-phrases: research this url, summarize page into notes, save article
  input-schema: '{"url":{"type":"string","description":"https URL to research"}}'
allowed-tools: BrowserAgent Native Notes navigate_to get_page_content summarize_url search_web list_notebooks create_notebook add_note_entry
---

# Research URL → notes

## Purpose
Turn a web page into a durable research note with source link.

## Steps
1. Extract the URL from the user message (must be http/https). If missing, ask for a URL.
2. Prefer browser tools when available:
   - `navigate_to` the URL
   - `get_page_content` and identify title + main points
3. Fallback if browser unavailable/fails: `summarize_url` on the same URL (optionally `search_web` for context).
4. Ensure notebook **Research** exists.
5. `add_note_entry` with:
   - Page title
   - Source URL
   - 5–10 bullet key points
   - One-line “why it matters” if obvious from the content
6. Reply with the summary and confirm save.

## Examples
- /research-url-to-notes https://example.com/article
- "Research this into my notes: https://..."

## Notes
- Do not invent content not present on the page.
- Skip login-walled pages gracefully and report the limitation.
""";
}
