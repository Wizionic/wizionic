---
id: documents
title: Documents
---

# Documents

The Documents library holds PDFs you saved from chat. Open it with the PDF icon in the navigation bar.

## Saving a PDF

When the model generates a PDF, click **Save to Documents** on the file card under its reply. You can also just ask: "save this PDF to my documents" — the model can call `save_pdf_to_library` itself. The library shows the file name, size, and date; **Download** saves a copy to your device.

## Privacy

Documents are encrypted with AES-256-GCM and stored under your account's prefix in IndexedDB (browser) or SQLite (desktop). The library is **device-local** — saved PDFs do not sync between devices yet. Deleting a document from the library does not remove its copy from the chat that created it.

A model can list your saved documents with `list_library_documents` when it needs to reference one.
