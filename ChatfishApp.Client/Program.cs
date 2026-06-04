using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<ChatfishApp.Client.Services.SidebarState>();
builder.Services.AddSingleton<ChatfishApp.Client.Services.WasmKeyStore>();
builder.Services.AddSingleton<ChatfishApp.Client.Services.WasmAiProviderService>();

// These must be Scoped because they depend on HttpClient (which is Scoped in Blazor WASM).
// Using Scoped here is fine and common in WASM apps — they live for the lifetime of the WASM app.
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmConversationStore>();
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmAuthService>();
builder.Services.AddScoped<ChatfishApp.Client.Services.WasmCryptoService>();

// App-level tools for agentic / tool-calling support in WASM (same tools as server, executed in browser).
builder.Services.AddSingleton<ChatfishApp.Services.Tools.IToolProvider, ChatfishApp.Services.Tools.DefaultToolProvider>();

// Configure the static HttpClient used by the WASM-side AppTools (for the proxied web-search and summarize-url).
// Setting BaseAddress ensures relative calls like "/api/tools/..." resolve to the host server
// (important for the tool proxy to work reliably from the browser).
ChatfishApp.Services.Tools.AppTools.HttpClient = new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    Timeout = TimeSpan.FromSeconds(30)
};

await builder.Build().RunAsync();
