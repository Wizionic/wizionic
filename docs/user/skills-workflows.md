---
id: skills
title: Skills, workflows, and agents
---

# Skills, workflows, and agents

## Skills

A skill is a short instruction pack (SKILL.md style) plus an allowed tool list. Run one from the Tools → Skills tab, or type `/skill-name` in chat.

Skills are stored on this device. They can sync if you enable the skills category on the Sync page.

## Workflows

A workflow is a **device-local schedule** that runs a skill. Workflows are **not** synced to other devices, and Agents does not start them.

Check the workflow tab for next-run time and the run log after something fires.

On Windows and Linux desktop, close-to-tray keeps this device online so scheduled workflows still fire until you Quit.

## Agents {#agents}

Agents lets one signed-in device run a skill on another device. Open [Tools](/tools) → **Agents**.

The **worker** is the device that owns the skill. It runs the skill with its own model, browser, and Home Assistant. The **orchestrator** sends the request and shows the steps that come back. This is not note sync. Chats and notes still move on [Devices and sync](/help/sync).

The worker picks a model it can run locally: the requested model, else its last-selected chat model, else its active profile, else its first available chat model. If the saved model only exists on another device, the run substitutes a local one and says so in the log. Skills sync between devices, so a device can publish a skill it has no workflow for; workflows stay device-local.

1. Sign in on each device with the same account. Each app, and the login server they use (your Home Server, or wizionic.com), needs this version of Wizionic. An older app shows **No skills published**.
2. On the device that should start runs, check **This device is the orchestrator**. Only one device can hold that. **Another device is the orchestrator** means leave it on the other device, or turn it off there first.
3. Online devices list the skills they have enabled (name and a short description, not the skill text). Pick a device, a skill, and a message, then **Run skill**. You cannot run a skill on this same device.
4. **Receipts** show each step as it arrives. The worker keeps the real log under Skills → Recent runs (source **Agent**). **Remove receipt** deletes only the copy on this device.
5. On a finished tool step, **Worked** tells the next run it can call that tool again. **Failed**, with a note, tells the next run the trace was wrong. Those notes are saved on the worker.

If the worker never answers, the receipt is **Failed**. If a run goes quiet, the receipt is **Behind**. The worker may still be running. Its log is the record.

**Could not publish this device** means the login server does not have the Agents API yet. Update and restart your Home Server, then check the box again. On wizionic.com, Agents works after that site is updated.

Close-to-tray keeps a desktop worker online so it can accept a run until you Quit.

## If a skill “has no tools”

The skill’s `allowed-tools` must match modules that are actually available (for example Browser tools only exist in the desktop app). A remote run uses the **worker’s** tools, not the orchestrator’s.
