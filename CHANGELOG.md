# Changelog

All notable changes to Wizionic are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Optional two-factor sign-in after a password: email code always, SMS via Twilio Verify if a phone is enrolled. Notebook/chat/album unlock stays password-only.

## [0.2.0] — 2026-08-15

First public source release.

### Added

- Public open-source documentation: license, privacy policy, terms, security policy, and self-host guide.
- Live `/privacy` and `/terms` pages for the hosted site (Google OAuth + SignPath).
- Unsigned GitHub Actions release workflow for Windows and Linux installers.

### Changed

- Desktop app updates now come from [GitHub Releases](https://github.com/Wizionic/wizionic/releases/latest) instead of wizionic.com. Login/sync stay on wizionic.com.
- Existing 0.1.x installs still check wizionic.com until they install a 0.2.x build once.
