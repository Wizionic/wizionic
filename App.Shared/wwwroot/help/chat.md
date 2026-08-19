---
id: chat
title: Chat
---

# Chat

Type in the box and send. The selected model answers using your conversation on this device.

A reply stops when the model finishes or when it hits **Settings → Reply length** (default 16,384 new tokens). That is not the same as the model’s context window.

## Model menu

**Profiles** appear first in the menu when you have created any on [Settings → Model profiles](/settings). A profile is a named stack (chat + image + speech). Palette, mic, Speak, and vision proxy follow the profile.

A **raw model** (xAI, Lemonade, Ollama, …) still chats and can call tools if that model has Tools. It does not pick up Imagine, mic, Speak, or a vision proxy from a profile. Use a profile for that stack. Selecting a raw image model still means Send generates with that model.

Models are grouped by source:

- **Ollama** — `http://localhost:11434` (or the URL on the Ollama page). No API key.
- **Lemonade** — `http://localhost:13305` by default. Configure on the Lemonade page.
- **Your cloud keys** — any OpenAI-compatible provider you add on [Cloud providers](/cloud-providers).
- **Hosted / proxied** — only if the site offers them. Those requests go through wizionic.com.

If a model cannot see images, a **vision proxy** (one per scope) can describe attachments as text first. Configure that on the Ollama or Lemonade page.

## What the model can do

If tools appear for this chat, the model may search the web, read a URL, use notes/calendar/gallery, Lemonade image/speech, or connectors you enabled. It should only call tools that are listed. See [Tools](/help/tools) and [Settings → tool routing](/help/tool-routing).

**Tools (N):** in the reasoning collapse is the *available* set for that turn, not what ran. `lemonade_generate_image` listed there does not mean Lemonade drew the picture.

If you selected a **cloud chat** model (e.g. Grok 4.6) and asked it to draw something, the turn should list `generate_image` (not `lemonade_generate_image`) and a line like `🎨 generate_image(xAI · grok-imagine-image)`.

A **direct** image (palette, or Send with an image model selected) never goes through that tool loop. The collapse then shows a single provenance line, for example `🎨 Image generate · xAI · grok-imagine-image`. Gallery `save_to_gallery` after that is a later chat turn.

## Attachments

Use **+ → Upload a file** in the chat box.

- **Markdown, plain text, and common source/config files** (`.md`, `.txt`, `.cs`, `.json`, `.py`, and similar) are decoded and sent as text with your message. Any chat model can read them — vision is not required. Large files are truncated (the first ~80,000 characters) so they fit the context window.
- **Images and PDFs** need a vision-capable model, or a **vision proxy** that describes the file first. Configure that on the Ollama or Lemonade page.
- Office files (`.docx`, `.xlsx`, `.pptx`) and other binaries are not supported yet.

Attachments stay in the conversation on this device. They are not uploaded to wizionic.com unless you chose a hosted/proxied model.

## Privacy

Message bodies are encrypted before they are written to IndexedDB (browser) or SQLite (desktop). Titles in the sidebar may be stored in cleartext so the list stays fast.
