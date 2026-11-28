---
id: tools
title: Tools, MCP, and connectors
---

# Tools, MCP, and connectors

Open [Tools](/tools). Tabs: **Tools**, **Skills**, **Workflows**, **Agents**.

## Built-in tools

Every built-in capability can be switched off under **Tools → Built-in**. A disabled tool is never offered to the model, so it cannot be called even by a skill. Home Assistant and the embedded browser are desktop-only; the web app does not list them. The desktop setup wizard asks whether you use Home Assistant before it installs local AI models.

When listed for a chat, the model may call web search, URL summarize, time, weather, and calculator. Search and summarize run through the Wizionic host so the browser does not hit CORS limits. Those requests include the query or URL you (or the model) sent.

`generate_pdf` creates a PDF from markdown entirely on your device — nothing is uploaded. The result appears under the reply as a downloadable card. See [Chat → PDF documents](/help/chat).

## MCP servers

Add an MCP server if you want extra tools (GitHub, Notion, and so on). Discovery is cached on the device. Tokens stay in the on-device key store.

## Connectors (OAuth)

Gmail, Google Calendar, GitHub, Notion, Stripe, and similar **app** logins. Wizionic’s client id lives on the host; **your access token stays on this device**.

Enable only what you need. The model should prefer the smallest set of tools that finishes the request.

## Skills, workflows, and agents

See [Skills, workflows, and agents](/help/skills). To run a skill on another device, see [Agents](/help/agents).
