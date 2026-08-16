---
id: local-ai
title: Ollama (local AI)
---

# Ollama (local AI)

[Ollama](https://ollama.com) runs open models on your machine. Wizionic talks to it directly. Settings on this page stay on the device.

## Install

1. Install Ollama from [ollama.com](https://ollama.com).
2. Leave the default URL `http://localhost:11434` unless you run it elsewhere.
3. Click **Refresh Models from Ollama**.
4. Pull a model from the list, or type a name (for example `llama3.2`).

## Browser on wizionic.com

Browsers block `http://localhost` from an `https://` page. On the machine that runs Ollama, allow this site:

```
[System.Environment]::SetEnvironmentVariable("OLLAMA_ORIGINS","https://wizionic.com","User")
```

Then restart Ollama. The desktop app does not need that — it is not a mixed-content page.

## Vision proxy

If you chat with a text-only model but attach an image, Wizionic can send the image to a vision-capable Ollama model first and inject the description. Pick one vision-proxy model on this page.

## What leaves the device

Ollama traffic goes to the URL you set (usually localhost). It does not go to wizionic.com.
