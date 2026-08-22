---
id: sync
title: Devices and sync
---

# Devices and sync

Signed-in devices see each other through the Wizionic server (presence only). **File contents move peer-to-peer over WebRTC**, encrypted. The server does not store your chats or notes.

## Use it

1. Sign in on each device with the same account.
2. Open [Devices & Sync](/sync). You should see the other device.
3. Choose what to sync (chats, notes, gallery, calendar, selected settings).
4. Pick a peer and sync. Only changed items are sent.

On Windows and Linux desktop, close-to-tray keeps this device online so other devices can still sync until you Quit.

## Login server (desktop)

The desktop app can use wizionic.com or a **Home Server** on this PC (`http://localhost:5150`). Set that under Settings → Login server. Pointing at the wrong host is a common reason 2FA or new APIs “do not exist.”

## AI server

A device with Ollama or Lemonade can serve as the AI backend for another peer over a dedicated data channel. Pick that on the Sync page.

## What the server sees

Presence, WebRTC signaling (offers, answers, ICE). Not message or note bodies.
