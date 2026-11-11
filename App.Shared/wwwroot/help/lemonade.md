---
id: lemonade
title: Lemonade (local AI)
---

# Lemonade (local AI)

[Lemonade Server](https://lemonade-server.ai/) is a local OpenAI-compatible server (often used on AMD). Default URL: `http://localhost:13305`.

## Install

- Desktop app: Settings → **Run setup wizard**, or install from [the Lemonade guide](https://lemonade-server.ai/docs/guide/install/).
- On Windows, if Lemonade would download the NVIDIA CUDA llama.cpp package, the wizard installs and selects Vulkan instead. That CUDA package needs Windows 11 22H2 to unpack, and a failed unpack hangs the first chat. AMD ROCm and CPU-only PCs are left on Lemonade’s own choice.
- Enter the base URL (and an API key only if you set `LEMONADE_API_KEY` on the server).
- **Save and Test**, then **Refresh Models from Lemonade**.
- Enable the chat models you want in the list. Image, speech, and other specialty models are configured on the same page.
- **Context size** is filled from Lemonade’s advertised window when you Refresh models. Chat loads the model with that size (Wizionic will not leave Lemonade at the 4096 auto-load default). Save the model only if you type a different number.

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
