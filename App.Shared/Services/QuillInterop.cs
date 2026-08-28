using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json.Serialization;

namespace App.Shared.Services;

internal static class QuillInterop
{
    private const string JsPrefix = "QuillFunctions.";

    public static ValueTask CreateQuillAsync(
        IJSRuntime js,
        ElementReference editorElement,
        ElementReference toolbarElement,
        bool readOnly,
        string placeholder,
        string theme,
        object? dotNetHelper = null,
        string? textChangeMethod = null) =>
        js.InvokeVoidAsync(
            $"{JsPrefix}createQuill",
            editorElement,
            toolbarElement,
            readOnly,
            placeholder,
            theme,
            dotNetHelper,
            textChangeMethod);

    public static ValueTask<string> GetHtmlAsync(IJSRuntime js, ElementReference editorElement) =>
        js.InvokeAsync<string>($"{JsPrefix}getQuillHTML", editorElement);

    public static ValueTask<string> GetTextAsync(IJSRuntime js, ElementReference editorElement) =>
        js.InvokeAsync<string>($"{JsPrefix}getQuillText", editorElement);

    public static ValueTask DestroyQuillAsync(IJSRuntime js, ElementReference editorElement) =>
        js.InvokeVoidAsync($"{JsPrefix}destroyQuill", editorElement);

    public static ValueTask InsertTextAsync(IJSRuntime js, ElementReference editorElement, string text) =>
        js.InvokeVoidAsync($"{JsPrefix}insertText", editorElement, text ?? "");

    public static ValueTask InsertHtmlAsync(IJSRuntime js, ElementReference editorElement, string html) =>
        js.InvokeVoidAsync($"{JsPrefix}insertHtml", editorElement, html ?? "");

    public static ValueTask InsertSttSegAsync(
        IJSRuntime js,
        ElementReference editorElement,
        string text,
        double startSeconds,
        string? audioId,
        bool newParagraph) =>
        js.InvokeVoidAsync(
            $"{JsPrefix}insertSttSeg",
            editorElement,
            text ?? "",
            startSeconds,
            audioId ?? "",
            newParagraph);

    public static ValueTask<SttCueAtCursor?> GetSttCueAtCursorAsync(IJSRuntime js, ElementReference editorElement) =>
        js.InvokeAsync<SttCueAtCursor?>($"{JsPrefix}getSttCueAtCursor", editorElement);

    public sealed class SttCueAtCursor
    {
        [JsonPropertyName("t")]
        public double T { get; set; }
        [JsonPropertyName("audio")]
        public string? Audio { get; set; }
    }
}