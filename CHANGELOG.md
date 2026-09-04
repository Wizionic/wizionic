# Changelog

All notable changes to Wizionic are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Calendar event **Alert** shows a dismissible alarm and repeats the sound (1 or 5 minutes, half-second gap). **Subscribe** adds an ICS/webcal feed (Canvas) and polls it.
- Calendar no longer auto-creates a **Personal** calendar on each device (that was duplicating via sync).
- Forgot password: **Forgot password?** on the Password tab (or **Forgot current password?** when signed in) emails a login code. Verifying it removes the password and turns two-factor off so you can **Add a password** again.
- WASM login on phone, tablet, Mac, or Chromebook recommends installing as a PWA, with Help steps for Chrome, Safari, Samsung Internet, Firefox, and Edge.
- Desktop sign-in: **More options** picks public wizionic.com or a local Home Server (same restart prompt as Settings).
- `scripts/install.ps1` (also `https://wizionic.com/install.ps1`): download `Wizionic-win-Setup.exe`, verify `SHA256SUMS`, `Unblock-File`, run the per-user Velopack installer.
- Home Assistant page: connection status, area-grouped devices with toggles, climate/cover/scene/script tools, room names in search via HA templates.
- Chat **Voice mode** (soundwave): listen for the assistant-name wake word, end on pause, send, speak the reply. Works without Home Assistant as normal chat.
- Settings → Voice: assistant name / wake word (migrated off the Home Assistant page).

- Settings → Model profiles: named stacks (chat, image, edit, TTS, STT, voice, routing override, vision proxy). Chat lists profiles at the top of the model menu. Palette, mic, Speak, and cloud `generate_image` use the selected profile only. A raw model in the list still chats and can call tools; it does not inherit Imagine, speech, or a vision proxy.
- Cloud providers is now a generic OpenAI-compatible add form (name, base URL, API key) instead of fixed Groq / Gemini / OpenRouter boxes. Refresh models, then Chat lists them as `cloud/{provider}/{model}` with tools, vision, image generate/edit, and STT/TTS when the vendor exposes those APIs.
- Desktop app checks GitHub Releases for a newer version at startup, when shown from the tray, or when started again while already running, and asks whether to install only when one is available.
- Settings → Reply length: global max output tokens for chat (default 16,384). This is an app generation cap, not the model context window.
- Chat attachments now include markdown, plain text, and common source/config files. The file is decoded and sent to the model with the message (any chat model, not only vision). Images and PDFs stay on the existing vision path; unsupported binaries are rejected in the UI.
- Optional two-factor sign-in after a password: email code always, SMS via Twilio Verify if a phone is enrolled. Notebook/chat/album unlock stays password-only.
- In-app Help (`/help` and a top-bar ?). Settings cards and setup pages open the matching article. No model required.

### Changed

- Calendar: hovering an event highlights it and shows the pointer; empty slots use the default cursor. Clicking the lower part of a multi-hour event edits it instead of opening a new one.
- Sign-in is required. Unauthenticated visitors only see the login landing page (and legal pages); chat, notes, gallery, and other app features are gated. Guest IndexedDB / guest-key migration is removed. Existing signed-in data under `u-{userId}-` is unchanged.
- Windows install on README and the login page now leads with `irm … | iex` instead of a browser `.exe` download. Unsigned Edge downloads still hit Mark of the Web / SmartScreen.

### Fixed

- WASM first paint no longer crashes: host SSR now registers a no-op calendar ticker (`ICalendarBackgroundService`) so `WorkflowDueBootstrap` can construct.
- Notes sync no longer sends the notebook id as the sidebar title, and a GUID title from a peer no longer overwrites a real name. Same guard for album titles.
- WebRTC answerer (Windows MAUI vs Linux Dell): incoming DataChannels are often already open before `onopen` is wired, so the answerer never sent its own manifest, calendar, or note updates and sat on `active: manifest` for 90s then tore down the live channel. Fire open if already open (same as WASM `readyState === 'open'`), send the pending outbound item on the first inbound DataChannel message, and do not close a live channel on handshake timeout.
- Linux MAUI DataChannel apply: Sync page `StateHasChanged` from SIPSorcery threads threw "current thread is not associated with the Dispatcher" and aborted message handling. Marshal UI notifies to the main thread; wrap change events so a UI exception cannot drop a calendar/note apply.
- Calendar LWW used `StartUtc` as the write clock, so future events tied on every edit and stale peers could win. Last-write is now `ModifiedUtc`/`DeletedAt`. Ignoring a stale remote calendar/event now pushes the newer local copy back.
- Sync protocol: treat matching content fingerprints as up to date (stop re-sending unchanged gallery images when only `LastUpdated` differs), start the 45s-style ack timer after send not before ICE, ignore acks for a different active item, and serialize coordinator queue mutations. Peer-online catch-up now includes calendars. Lemonade Base URL / API key / model list saves trigger settings sync. WASM default device names use a real bullet (`Chrome • Windows`) instead of mojibake. Desktop “Used” storage is live chat+notes+gallery content; SQLite WAL/freelist is shown separately as on-disk overhead.
- Settings LWW no longer lets a never-saved peer (ticks 0/1, factory localhost) overwrite a device that already has a real Lemonade/Ollama URL. The Lemonade page reloads in-memory settings (does not reset WASM config to localhost). Incoming settings only ack after apply/ignore; DataChannel .NET callbacks are deferred off the WebRTC stack so Opera/WASM can write localStorage. CORS / mixed-content checks are display-only and do not block applying a synced Base URL.
- WebRTC: only the lexicographically smaller device id creates offers; the other answers or sends `webrtc-need-offer`. Stops both sides offering at once (answer applied, DataChannel never opens, 90s timeouts). DataChannels stay open between items.
- WebRTC ICE: browser callbacks are deferred so Chromium ICE is not lost during `setLocalDescription`; candidates that arrive before the peer connection exists are queued; Chromium `*.local` mDNS host candidates are rewritten to LAN IPv4 on MAUI. Offer/answer wait for ICE gathering so host candidates are inside the SDP (Chrome was applying the answer with no local candidates on the wire).
- Direct image generate/edit now records the real catalog model (`cloud/…` or `lemonade/…`) and a one-line provenance trace instead of always labeling the result as Lemonade.
- With a cloud chat model selected (e.g. Grok 4.6), image-intent turns attach `generate_image` for that provider instead of `lemonade_generate_image`. The tool trace names the vendor and model.
- Chat again uses the normal streaming path for Lemonade (thinking stays on). The thinking-off raw HTTP client is documented as Help-only.

## [0.2.0] — 2026-08-15

First public source release.

### Added

- Public open-source documentation: license, privacy policy, terms, security policy, and self-host guide.
- Live `/privacy` and `/terms` pages for the hosted site (Google OAuth + SignPath).
- Unsigned GitHub Actions release workflow for Windows and Linux installers.

### Changed

- Desktop app updates now come from [GitHub Releases](https://github.com/Wizionic/wizionic/releases/latest) instead of wizionic.com. Login/sync stay on wizionic.com.
- Existing 0.1.x installs still check wizionic.com until they install a 0.2.x build once.
