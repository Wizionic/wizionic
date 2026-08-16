---
id: tools
title: Tools, MCP, and connectors
---

# Tools, MCP, and connectors

Open [Tools](/tools). Tabs: **Tools**, **Skills**, **Workflows**.

## Built-in tools

When listed for a chat, the model may call web search, URL summarize, time, weather, and calculator. Search and summarize run through the Wizionic host so the browser does not hit CORS limits. Those requests include the query or URL you (or the model) sent.

## MCP servers

Add an MCP server if you want extra tools (GitHub, Notion, and so on). Discovery is cached on the device. Tokens stay in the on-device key store.

## Connectors (OAuth)

Gmail, Google Calendar, GitHub, Notion, Stripe, and similar **app** logins. Wizionic’s client id lives on the host; **your access token stays on this device**.

Enable only what you need. The model should prefer the smallest set of tools that finishes the request.

## Skills and workflows

See [Skills and workflows](/help/skills).
