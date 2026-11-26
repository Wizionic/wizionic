---
id: documents
title: Projects and files
---

# Projects and files

Open **Projects** (the folder icon) and select **＋** beside Projects. Enter a name, optionally add a description and files, then select **Create**. Click the project title to edit its details. Older PDFs are under **Unfiled documents**.

## Chats

Each project can hold several chats. Under the project in the left list, the **Chats** row has **＋** for a new chat and a chat icon to **add an existing chat** from your general list. Chats appear as sub-items; selecting one opens it with the project's files and instructions. An added chat keeps its history and sync identity; use its **✕** to return it to the general list without deleting it.

## Files and answers

Open **Files → ＋** to add PDFs, Markdown, text, HTML, Word (`.docx`), or images (32 MB per file). Select a filename to preview it beside chat. PDF controls change pages and zoom; the download icon exports a copy. HTML and Word open as a readable, script-free page; Markdown renders formatted. Images can be attached to a chat for vision analysis.

Text is indexed automatically on this device, including up to two million characters per file. HTML and Word are indexed by their visible text. Scanned PDFs without a text layer are preview-only. Search works without an embedding model; configuring a local Help embedding model also enables semantic project search. Source buttons under an answer open the passages supplied to the model, at their original pages. Deleting a file removes it from future retrieval. Existing answers can still contain quotations.

## Linking notes

In **Files**, select the note icon to link an existing notebook or a single entry. The link opens a read-only preview beside chat and follows the note as it changes; use the open icon to edit it in Notes. Unlinking removes it from the project but keeps the note. When you ask project chat to save something as a note, Wizionic adds the link under **Files** and as a preview card in the chat. Links sync with the project; the note itself syncs through Notes. A linked note shows **Waiting for note sync** until it arrives on this device, and protected notebooks must be unlocked in Notes before they can be previewed.

## Creating outputs

Choose a **Draft** format (PDF, Markdown, TXT, or HTML) before sending a request. Wizionic runs relevant tools first, then streams the document and opens the finished file in the chosen format. For example, ask it to search the web for recent news or check which lights are on and include the results in a report. Live tools require a model that supports tool calls. Project chat saves generated files automatically. Ordinary chat can still save PDFs to the unfiled library.

## Workflows and devices

Link a device workflow from the **Workflows** tab. Its definition travels with the project. **Run now** saves its output and transcript in the project; the referenced skill and a suitable model must be available on the executing device. Scheduled runs stay on the device where the workflow was configured.

Project data is stored under your account in IndexedDB (web) or SQLite (desktop), with file contents and search indexes encrypted. Enable **chat history auto-sync** and select your devices in Sync, or sync chat history manually. Both peers must be online for transfer; completed file parts are retained between connections. Each device builds its own search index. Relevant excerpts are included when you send a project question to your selected chat model, including cloud models.
