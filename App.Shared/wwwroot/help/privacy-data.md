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

## Guest vs signed in

Guest data uses a `wasmchat-` prefix. After login, data can move under `u-{userId}-`. Guest data never leaves the device unless you sign in and migrate it.
