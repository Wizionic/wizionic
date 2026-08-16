# User help (source)

How-to articles for the in-app Help panel. `ARCHITECTURE.md` is not part of this set.

Ship the same files in `App.Shared/wwwroot/help/` so WASM and desktop can load them as static assets. Edit both copies (or copy this folder over `wwwroot/help`) when you change an article.

RAG / embeddings are a follow-up. This folder is browse + keyword search only for now.
