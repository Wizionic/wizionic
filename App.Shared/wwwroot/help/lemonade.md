---
id: lemonade
title: Lemonade (local AI)
---

# Lemonade (local AI)

[Lemonade Server](https://lemonade-server.ai/) is a local OpenAI-compatible server (often used on AMD). Default URL: `http://localhost:13305`.

## Install

- Desktop app: Settings → **Run setup wizard**, or install from [the Lemonade guide](https://lemonade-server.ai/docs/guide/install/).
- Enter the base URL (and an API key only if you set `LEMONADE_API_KEY` on the server).
- **Test connection**, then **Refresh Models from Lemonade**.
- Enable the chat models you want in the list. Image, speech, and other specialty models are configured on the same page.

## Browser on wizionic.com

Allow the site origin on Lemonade (CORS). Mixed content may still block `http://localhost` from HTTPS:

```
[System.Environment]::SetEnvironmentVariable("LEMONADE_ALLOWED_ORIGINS","https://wizionic.com","User")
```

The desktop app talks to Lemonade without that browser restriction.

## Images and speech

When those services are enabled, chat can generate or edit images and use text-to-speech. Images appear in the chat. Use **save to gallery** only if you want to keep one.

## What leaves the device

Requests go to your Lemonade URL. They are not sent to wizionic.com unless you pick a hosted/proxied model instead.
