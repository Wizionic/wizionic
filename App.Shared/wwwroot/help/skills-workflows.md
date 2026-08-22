---
id: skills
title: Skills and workflows
---

# Skills and workflows

## Skills

A skill is a short instruction pack (SKILL.md style) plus an allowed tool list. Run one from the Tools → Skills tab, or type `/skill-name` in chat.

Skills are stored on this device. They can sync if you enable the skills category on the Sync page.

## Workflows

A workflow is a **device-local schedule** that runs a skill. Workflows are **not** synced to other devices.

Check the workflow tab for next-run time and the run log after something fires.

On Windows and Linux desktop, close-to-tray keeps this device online so scheduled workflows still fire until you Quit.

## If a skill “has no tools”

The skill’s `allowed-tools` must match modules that are actually available (for example Browser tools only exist in the desktop app).
