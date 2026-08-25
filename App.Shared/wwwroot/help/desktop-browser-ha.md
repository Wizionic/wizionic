---
id: desktop
title: Desktop — browser and Home Assistant
---

# Desktop — browser and Home Assistant

These exist in the **Windows and Linux apps**, not in the website tab.

## Embedded browser

Open the browser icon in the top bar (if shown). Chat can navigate, read the page, click, and fill fields when those tools are listed. Useful for “open this site and summarize it” on the desktop.

## Home Assistant {#home-assistant}

Open **Home Assistant** from the desktop icons (house). This page talks to your Home Assistant instance on the local network. Control does not run in the website tab.

Enter the **Base URL** you use in a browser (example: `http://192.168.4.23:8123`). The right pane embeds that dashboard on desktop (Home Assistant blocks iframes in a normal browser).

Then paste a **long-lived access token** (see below). Save or **Test connection**. **Refresh devices** reloads the list. **Disconnect** removes the URL and token from this device.

The token and URL stay on this device. They are not sent to wizionic.com.

The wake word used in chat and Voice mode is **Settings → Voice → Assistant name**, not a field on this page.

## Long-lived access token {#token}

A long-lived access token is a password Home Assistant issues for apps. Wizionic uses it only from this PC to call the HA REST API on your LAN.

Create one in Home Assistant:

1. Open Home Assistant (the embed on the right, or in a browser at your Base URL).
2. Click your **username** at the bottom of the left sidebar (Profile).
3. Open the **Security** tab.
4. Scroll to **Long-lived access tokens**.
5. Click **Create Token**. Name it something like `Wizionic`.
6. **Copy the token immediately.** Home Assistant shows it only once.
7. Paste it into Wizionic’s **Long-lived access token** field and Save or Test.

If Test says Unauthorized, create a new token and paste it again. Treat the token like a password: do not put it in screenshots or chat.

Official HA notes: [authentication / your profile](https://www.home-assistant.io/docs/authentication/#your-account-profile).

## Devices {#devices}

After a successful connection, devices are **grouped by area** (kitchen, living room, …). Lights, switches, covers, and locks can be toggled on this page. Anything else is still available in chat.

If a room is missing, assign the entity to an area in Home Assistant, then **Refresh devices**. Search in chat also matches area names (`turn off the kitchen lights`).

## Chat and Voice {#chat}

In Chat, address the assistant name from Settings → Voice, then the command: `Hey Bro, play music on the Denon AVR`. Follow-ups like `make it louder` work for a while in the same chat after a successful device command.

Voice mode (soundwave on the chat box) listens for that name, captures until you pause, then sends the turn. By default it waits for the wake word again so it does not transcribe music or background noise.

Chat can list and control lights, media players, climate, covers, scenes, and scripts when Home Assistant is connected.

## Setup wizard

Settings → **Run setup wizard** can install a Home Server (local login site), Lemonade, and/or Ollama on this PC. After a Home Server install, other devices use the network URL under Settings → Home Server.
