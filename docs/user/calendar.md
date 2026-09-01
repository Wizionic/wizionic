---
id: calendar
title: Calendar
---

# Calendar

Calendars and event bodies live on this device. Event details are encrypted; names and dates may be listed in cleartext.

## Basics

- Create a calendar, then add events. Times are local unless you say otherwise.
- You can connect Google Calendar as a **connector** (Tools) if you want that account’s events in chat tools. Your Google access token stays on this device.
- Calendar **⋮ → Import .ics** adds events from a file once. To keep a school/Canvas calendar up to date, use **Subscribe**.

## Event alerts {#alerts}

On an event, **Alert** shows a dismissible alarm and repeats the sound (with a half-second gap) for **1 minute** or **5 minutes**. Wizionic must be running (desktop can stay in the tray). Tap **Play** to preview a sound. SMS and email are not available yet.

All-day events treat “at time of event” as 9:00 in the morning locally.

## Subscribe {#subscribe}

Paste a `webcal://` or `https://` `.ics` URL (Canvas → Calendar → Calendar Feed). We fetch it on a timer (every **6 hours** unless the feed sets `REFRESH-INTERVAL`, never more often than every 15 minutes) and replace that calendar’s events. The feed is read-only; you can still set alerts, hide it, or unsubscribe.

## Chat tools

When Calendar tools are listed, the model can list calendars, list events, and add or update events. Confirm times before it writes.
