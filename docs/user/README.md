# User help (source)

How-to articles for the in-app Help panel. `ARCHITECTURE.md` is not part of this set.

Ship the same files in `App.Shared/wwwroot/help/` so WASM and desktop can load them as static assets. Edit both copies (or copy this folder over `wwwroot/help`) when you change an article.

Browse and keyword search always work with no model. Optional Ask in Help uses a user-chosen chat model plus these articles (and, on desktop, an optional local embeddings index).
