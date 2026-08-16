---
id: settings
title: Settings
---

# Settings

This page stays local except the **login server** URL on desktop (that is where the app signs in).

## Login server (desktop only) {#login-server}

Where this app authenticates and announces presence. Public default is `https://wizionic.com`. A Home Server uses `http://localhost:5150`. After you change it, restart if the app asks you to.

The website always uses its own origin.

## Appearance {#appearance}

Theme follows a palette or the OS. On desktop you can put the main icons on the **top** or as a **left** rail.

## About you {#about-you}

Optional name and occupation injected into the system prompt when customization is on. Stored on this device (and synced if you enable settings sync).

## Memories {#memories}

Short facts sent with the system prompt. They are not uploaded to wizionic.com as a memory service.

## Tool routing {#tool-routing}

Decides which tool **modules** (Native, Lemonade, Gallery, Notes, and so on) are attached before the model runs.

- **Rules** — instant, no extra model call. Default.
- **Hybrid** — rules for clear cases; a small model when the message is ambiguous.
- **AI router** — always classify with the routing model; falls back to rules if that fails.

This is not the same as enabling MCP servers. Routing only chooses among modules that are already available.

## Help answers {#help-answers}

Optional. The Help panel can **Ask** a chat model using only the shipped help articles.

- **Answer model** — writes the reply. Off keeps Help as browse + search.
- **Embeddings model** — optional local Ollama or Lemonade embeddings model. Builds a small index in `help_rag.db` on desktop (not your chat history). Without it, Ask still works from keyword matches.
- Changing the answer model does not rebuild the index. Changing the embeddings model or the articles does.

A cloud answer model sends the question and a few article excerpts to that provider. Browse never needs a model.

## System prompt {#system-prompt}

Extra instructions for every chat. The app also sends a built-in default (date, how storage works, tool habits). Your text is added on this device.
