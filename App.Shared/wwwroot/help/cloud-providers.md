---
id: cloud-providers
title: Cloud providers
---

# Cloud providers

Wizionic is built for local AI. You can still plug in any **OpenAI-compatible** cloud endpoint so Chat can use that vendor’s models from this device.

## Add a provider

1. Open [Cloud providers](/cloud-providers).
2. Enter a display name, the API base URL (include `/v1` when the vendor uses it), and your API key.
3. Optionally pick a template from the list (xAI, OpenAI, Groq, OpenRouter, Gemini, and others) to fill the name and URL. You can still edit them.
4. Save and **refresh models**. Chat lists that provider’s models.

Keys stay in this app’s key store. If you are signed in they are encrypted on the device.

Example for xAI / Grok: base URL `https://api.x.ai/v1`.

## What Chat can do

After a refresh, capabilities follow the selected model and what that provider exposes:

- **Chat models** — streaming replies, tools when the model supports them, image attachments when vision is on.
- **Image models** — Send generates; palette / edit when the provider has generate or edit models.
- **Voice** — mic (speech-to-text) and Speak (text-to-speech) when the provider has those endpoints. This is not a live Realtime voice session.

### What the checkboxes mean

**Refresh models** guesses flags from the vendor catalog. They are “what this row *is*”, not a list of features you must turn on by hand.

- **Tools / Vision** — chat models. Leave them on for Grok chat (`grok-4.6`, `latest`, …) unless a model cannot do that job.
- **Image / Edit** — only on image generators (`grok-imagine-image`). Do not check these on a chat model.
- **TTS / STT** — only when the *model id* is a speech deployment (Whisper, Kokoro, `tts-1`). xAI almost never lists those as models. Assign xAI TTS/STT on [Settings → Model profiles](/settings), not by checking boxes on `grok-4.6`.

You can override a wrong guess. You do not need to reverse-engineer every Grok chat model for voice.

## Hosted / proxied models

Some models on wizionic.com are called through `/api/proxy/chat` because they use a **server** key. Those requests go through the host. Prefer a user-keyed or local model when you want the bytes to stay on this machine.

## What leaves the device

- **User-keyed models:** this device → the vendor, using your key.
- **Proxied models:** this device → wizionic.com → the vendor.
- **Local Ollama/Lemonade:** this device → localhost (or the URL you set).
