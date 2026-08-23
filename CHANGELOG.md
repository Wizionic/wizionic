# Changelog

All notable changes to Wizionic are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `scripts/install.ps1` (also `https://wizionic.com/install.ps1`): download `Wizionic-win-Setup.exe`, verify `SHA256SUMS`, `Unblock-File`, run the per-user Velopack installer.
- Home Assistant page: connection status, area-grouped devices with toggles, climate/cover/scene/script tools, room names in search via HA templates.
- Chat **Voice mode** (soundwave): listen for the assistant-name wake word, end on pause, send, speak the reply. Works without Home Assistant as normal chat.
- Settings → Voice: assistant name / wake word (migrated off the Home Assistant page).

- Settings → Model profiles: named stacks (chat, image, edit, TTS, STT, voice, routing override, vision proxy). Chat lists profiles at the top of the model menu. Palette, mic, Speak, and cloud `generate_image` use the selected profile only. A raw model in the list still chats and can call tools; it does not inherit Imagine, speech, or a vision proxy.
- Cloud providers is now a generic OpenAI-compatible add form (name, base URL, API key) instead of fixed Groq / Gemini / OpenRouter boxes. Refresh models, then Chat lists them as `cloud/{provider}/{model}` with tools, vision, image generate/edit, and STT/TTS when the vendor exposes those APIs.
- Desktop app checks GitHub Releases for a newer version at startup and asks whether to install only when one is available.
- Settings → Reply length: global max output tokens for chat (default 16,384). This is an app generation cap, not the model context window.
- Chat attachments now include markdown, plain text, and common source/config files. The file is decoded and sent to the model with the message (any chat model, not only vision). Images and PDFs stay on the existing vision path; unsupported binaries are rejected in the UI.
- Optional two-factor sign-in after a password: email code always, SMS via Twilio Verify if a phone is enrolled. Notebook/chat/album unlock stays password-only.
- In-app Help (`/help` and a top-bar ?). Settings cards and setup pages open the matching article. No model required.

### Changed

- Windows install on README and the login page now leads with `irm … | iex` instead of a browser `.exe` download. Unsigned Edge downloads still hit Mark of the Web / SmartScreen.

### Fixed

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
