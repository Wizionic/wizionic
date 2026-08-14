# Privacy Policy

**Effective date:** 14 August 2026  
**Operator:** Daniel Goodwin, operating as Wizionic  
**Contact:** daniellgoodwin@protonmail.com  
**Site:** https://wizionic.com  
**Source:** https://github.com/Wizionic/wizionic

This policy describes the hosted Wizionic service at wizionic.com and the official desktop/browser clients. It is meant to match the public source code.

## Short version

Wizionic is local-first. Chat messages, notes, gallery images, and calendar event bodies are stored on your device and encrypted at rest with AES-256-GCM. The hosted service is not a chat archive. It handles accounts, device presence, WebRTC signaling, optional tool and model proxies, and OAuth app credentials.

There is no advertising suite and no third-party analytics pixel.

## What the hosted service stores

If you create an account on wizionic.com, the server may store:

- Email address
- Optional password hash (if you set a password)
- Magic-link / login-code token until it is used or expires
- Auth cookie `AppAuth` (HttpOnly, first-party, 10-year sliding session)
- A per-user local encryption key so your own devices can decrypt the same local data
- Optional cloud provider API keys (`UserProviderKeys`) **only if you choose to save them on the server**
- OAuth *application* client IDs and secrets (Google, GitHub, Notion, Stripe) that belong to the Wizionic app, not your personal access tokens
- SignalR presence and WebRTC signaling messages (offers, answers, ICE candidates — not chat payloads)
- Logs of tool-proxy requests you trigger (`search_web`, `summarize_url`, and similar)
- Proxied model requests if you pick a hosted/proxied model
- Transactional email metadata via Brevo (magic-link delivery)

Optional user-saved provider keys on the server are stored as configured today. Treat server-stored keys as sensitive and prefer keeping keys on the device when you can.

## What stays on the device

- Chat, notes, gallery, and calendar **content** (encrypted before it is written to IndexedDB or SQLite)
- Metadata such as titles and dates is stored in cleartext locally so the sidebar can list items quickly
- Guest data under the `wasmchat-` namespace
- Authenticated local data under `u-{userId}-`
- User OAuth *access* tokens and MCP tokens in the on-device key store
- Device-local workflows / schedules (they are not synced)

## Cookies

Wizionic sets one first-party cookie:

| Cookie | Purpose |
|---|---|
| `AppAuth` | Login session. HttpOnly. Strictly necessary. 10-year sliding expiration. |

There are no advertising cookies, no tracking pixels, and no third-party analytics cookies. A separate cookie banner is not used because this is a strictly necessary first-party session cookie.

## Google connectors (Limited Use)

If you connect Gmail or Google Calendar, Wizionic requests only the scopes needed for the connector you enabled (including Gmail read/send/modify and Calendar scopes where those features are offered).

Wizionic's use of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements:

- Gmail and Calendar data is used only to provide the user-facing connector you enabled.
- That data is not sold.
- That data is not used for advertising.
- That data is not used to train unrelated models.

Your Google *access* tokens stay on the client. Wizionic's Google *app* client ID and secret stay on the host.

## Other network activity the desktop app may make

The desktop app does not transfer information to other networked systems unless you (or the installer/updater) ask it to. Exceptions you can trigger:

- Signing in to wizionic.com
- Sync presence and WebRTC signaling
- User-configured local or cloud models
- User-enabled connectors and MCP tools
- Built-in tools such as web search or URL summarize, when the model calls them
- Checking for application updates

## Email

Magic-link login codes are sent through Brevo from `no-reply@wizionic.com`. Brevo sees the destination address and the message content of that transactional email.

## No advertising / no analytics suite

The product does not include an advertising SDK or a third-party analytics suite. If that changes, this policy will be updated first.

## Account deletion and local wipe

- **Hosted account:** email daniellgoodwin@protonmail.com from the address on the account and ask for deletion. I will delete the server-side user row and related server records.
- **Local data:** sign out, then clear the app's local data (browser site data / IndexedDB for WASM; uninstall or delete the app data directory on desktop). Guest data never leaves the device unless you later sign in and choose to migrate it.

## Children

Wizionic is not directed at children under 16.

## Changes

Material changes will be posted on this page with an updated effective date.

## Source of truth

Canonical text lives in this file in the public repository. The live page is [https://wizionic.com/privacy](https://wizionic.com/privacy).
