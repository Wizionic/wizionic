---
id: chat
title: Chat
---

# Chat

Type in the box and send. The selected model answers using your conversation on this device.

## Model menu

Models are grouped by source:

- **Ollama** — `http://localhost:11434` (or the URL on the Ollama page). No API key.
- **Lemonade** — `http://localhost:13305` by default. Configure on the Lemonade page.
- **Your cloud keys** — Groq, OpenRouter, Gemini, and others from [Cloud providers](/cloud-providers).
- **Hosted / proxied** — only if the site offers them. Those requests go through wizionic.com.

If a model cannot see images, a **vision proxy** (one per scope) can describe attachments as text first. Configure that on the Ollama or Lemonade page.

## What the model can do

If tools appear for this chat, the model may search the web, read a URL, use notes/calendar/gallery, Lemonade image/speech, or connectors you enabled. It should only call tools that are listed. See [Tools](/help/tools) and [Settings → tool routing](/help/tool-routing).

## Attachments

Images and PDFs stay in the conversation on this device. They are not uploaded to wizionic.com unless you chose a hosted/proxied model.

## Privacy

Message bodies are encrypted before they are written to IndexedDB (browser) or SQLite (desktop). Titles in the sidebar may be stored in cleartext so the list stays fast.
