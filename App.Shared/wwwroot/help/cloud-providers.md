---
id: cloud-providers
title: Cloud providers
---

# Cloud providers

Wizionic is built for local AI. You can still plug in any **OpenAI-compatible** cloud endpoint so Chat can use that vendor’s models from this device.

## Add a provider

1. Open [Cloud providers](/cloud-providers).
2. Optionally search the **Template** list. Templates come from [models.dev](https://models.dev) and fill the display name, base URL, and provider id. You can still edit them.
3. Enter the API key (and change the URL if your endpoint differs).
4. Save and **Refresh models**, then **Add** the models you want (below).

Keys stay in this app’s local key store and are never sent to models.dev.

Example for xAI / Grok: base URL `https://api.x.ai/v1`.

## Models.dev metadata

[models.dev](https://models.dev) is an open catalog of model names, context windows, output limits, modalities, and pricing. Wizionic uses it to fill gaps the vendor API does not report.

- **Refresh models** uses the vendor’s model list first. models.dev fills context, output limits, tools/vision, and image/speech flags when they are missing.
- **Sync metadata** re-applies models.dev values to models you already refreshed. Your **Add** choices and capability overrides still win.
- Each matched model shows a small **models.dev** badge and its context/output size.
- Providers that are not listed on models.dev (for example CometAPI) still get model-name matching, including common reseller aliases and snapshot/date suffixes. The provider row says **name-matched N** or **not in models.dev**; unmatched rows keep `CTX 0`, which you can edit.
- The catalog is fetched through the Wizionic host when available (otherwise directly from models.dev), cached for a day, and reuses cached data when offline. Only public metadata is requested — no API key, prompt, or chat content is sent.

## Add models to your lists

Refresh lists every model the vendor exposes — often hundreds. Only models you **Add** appear in Chat, Skills, Workflows, and Settings pickers.

1. Expand a provider with **Models**.
2. Click **Add** on each model you want; an **Added** badge confirms it. Use the filter box or **Added only** to find rows.
3. Click the trash can to remove a model from the pickers again.

Refresh keeps your Add choices and capability checkboxes; new vendor models start un-added.

## What Chat can do

After a refresh, capabilities follow the selected model and what that provider exposes:

- **Chat models** — streaming replies, tools when the model supports them, image attachments when vision is on.
- **Image models** — Send generates; palette / edit when the provider has generate or edit models.
- **Voice** — mic (speech-to-text) and Speak (text-to-speech) when the provider has those endpoints. This is not a live Realtime voice session.

### What the checkboxes mean

**Refresh models** guesses flags from the vendor catalog and models.dev. They are “what this row *is*”, not a list of features you must turn on by hand.

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
