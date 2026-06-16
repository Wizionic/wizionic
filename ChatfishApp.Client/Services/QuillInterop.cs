using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ChatfishApp.Client.Services;

internal static class QuillInterop
{
    private const string JsPrefix = "QuillFunctions.";

    public static ValueTask CreateQuillAsync(
        IJSRuntime js,
        ElementReference editorElement,
        ElementReference toolbarElement,
        bool readOnly,
        string placeholder,
        string theme) =>
        js.InvokeVoidAsync(
            $"{JsPrefix}createQuill",
            editorElement,
            toolbarElement,
            readOnly,
            placeholder,
            theme);

    public static ValueTask<string> GetHtmlAsync(IJSRuntime js, ElementReference editorElement) =>
        js.InvokeAsync<string>($"{JsPrefix}getQuillHTML", editorElement);

    public static ValueTask<string> GetTextAsync(IJSRuntime js, ElementReference editorElement) =>
        js.InvokeAsync<string>($"{JsPrefix}getQuillText", editorElement);

    public static ValueTask DestroyQuillAsync(IJSRuntime js, ElementReference editorElement) =>
        js.InvokeVoidAsync($"{JsPrefix}destroyQuill", editorElement);
}