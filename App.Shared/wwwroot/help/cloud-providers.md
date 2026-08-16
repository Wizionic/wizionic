---
id: cloud-providers
title: Cloud providers
---

# Cloud providers

Add your own API keys so Chat can call Groq, OpenRouter, Gemini, and similar services **from this device**.

## Add a key

1. Open [Cloud providers](/cloud-providers).
2. Paste the key for that provider and save.
3. The model menu in Chat lists that provider’s models.

Keys are stored in this app’s key store. If you are signed in they are encrypted on the device. You can optionally save a key on the Wizionic server so another of your devices can import it — only do that if you want that convenience.

## Hosted / proxied models

Some models on wizionic.com are called through `/api/proxy/chat` because the browser cannot reach the vendor directly. Those requests (and your prompt) go through the host. Prefer a user-keyed or local model when you want the bytes to stay on this machine.

## What leaves the device

- **User-keyed models:** this device → the vendor, using your key.
- **Proxied models:** this device → wizionic.com → the vendor.
- **Local Ollama/Lemonade:** this device → localhost (or the URL you set).
