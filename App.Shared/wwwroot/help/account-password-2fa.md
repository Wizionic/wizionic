---
id: account
title: Account, password, and 2FA
---

# Account, password, and 2FA

## Sign in

- **Login code (email)** — we email a code and a link. Fine when 2FA is off.
- **Password** — if you set one. Faster than waiting for email.

Guest mode needs no account. Sign in when you want the same encryption key on more than one device.

## Password

Optional. Used to:

- Sign in without an email code
- Unlock protected notebooks, chats, and albums

Requirements are shown on the form (length, a digit, and a capital or symbol). Common passwords are rejected.

Unlocking a notebook still asks **only** for this password. It does not ask for 2FA.

## Two-factor sign-in

On the account page, **Two-factor sign-in** (hidden behind a link). When it is on:

1. Enter email and password.
2. Enter an email code, or an SMS code if you enrolled a phone.

A cold click of a magic link does **not** sign you in. Email codes complete the second step after the password.

SMS uses Twilio Verify on the host you are pointed at. If that host has no Twilio config, you can still use email as the second factor.

The desktop app’s **login server** must be the host that has 2FA (local `dotnet run` vs wizionic.com). See [Sync](/help/sync).
