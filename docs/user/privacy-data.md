---
id: privacy-data
title: What stays on this device
---

# What stays on this device

Full legal text: [Privacy](/privacy) and [Terms](/terms).

## On this device

- Chat, notes, gallery, and calendar **content** (AES-256-GCM before write)
- Your cloud API keys (on-device key store; they can sync between your devices over WebRTC)
- Connector access tokens and MCP tokens
- Device-local workflows

## On the Wizionic host (if you have an account)

- Email, optional password hash, optional 2FA flag and phone
- A per-user encryption key so your devices can read the same data
- Presence and WebRTC signaling (not chat payloads)
- Transactional email (login codes) via Brevo
- SMS codes via Twilio if you enrolled a number
- Tool-proxy and model-proxy traffic you actually trigger

## Report inappropriate content {#report-content}

Assistant messages have a ⋮ menu with **Report this response**. Settings has the same form for image, speech, browser, or Home Assistant output with no single message.

The report opens an email to daniellgoodwin@protonmail.com with app version, time, model id, and your description. It does not attach the chat or note body. Wizionic does not run a central moderation pipeline on models you run locally.

## Signed-in data

App data is stored under `u-{userId}-` on this device. Sign-in is required; there is no guest workspace.
