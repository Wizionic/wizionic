using App.Core.Storage;
using App.Shared.Services;
using Microsoft.AspNetCore.Components;
using Markdig;

namespace App.Shared.Components;

public partial class ChatPage
{
    [Inject] ProjectRetrieval Retrieval {get;set;}=default!;
    [Inject] ProjectStore Projects {get;set;}=default!;
    [Parameter] public string? ProjectId {get;set;}
    [Parameter] public string? ProjectChatId {get;set;}
    /// <summary>"" (off), "pdf", "md", "txt", or "html".</summary>
    [Parameter] public string DraftFormat {get;set;}="";
    [Parameter] public EventCallback<string> OnDraft {get;set;}
    [Parameter] public EventCallback OnProjectSaved {get;set;}
    [Parameter] public EventCallback<ProjectFile> OnOutput {get;set;}
    [Parameter] public EventCallback<ProjectSource> OnSource {get;set;}
    [Parameter] public EventCallback<Attachment> OnPreview {get;set;}
    public void AttachProjectImage(Attachment image){pendingFiles.Add(image);StateHasChanged();}
    private List<ProjectSource>? pendingSources;
    private byte[]? inlinePdf; private string inlinePdfName="";
    async Task PreviewPdf(Attachment att){if(ProjectId!=null)await OnPreview.InvokeAsync(att);else{inlinePdf=Convert.FromBase64String(att.DataBase64);inlinePdfName=att.Name;StateHasChanged();}}
    async Task<List<LocalConvo>> ScopedChatIndex()
    {
        var all = await ConversationStore.LoadIndexAsync();
        if (ProjectId == null)
        {
            var linked = await Projects.AllLinkedChatIdsAsync();
            return all.Where(c => !ProjectStore.IsChat(c.Id) && !linked.Contains(c.Id)).ToList();
        }
        var prefix = ProjectStore.ChatPrefix(ProjectId);
        var mine = await Projects.LinkedChatIdsAsync(ProjectId);
        return all.Where(c => c.Id.StartsWith(prefix) || mine.Contains(c.Id)).ToList();
    }
    async Task<List<ChatMessage>> ProjectRequest(bool drafting, CancellationToken ct){
        pendingSources=null;
        if(ProjectId==null)return messages;
        var history=messages.ToList(); var ix=history.FindLastIndex(m=>m.Role=="user");if(ix<0)return history;
        var budget=_contextLimit is >0 ? Math.Clamp((_contextLimit.Value-(_contextUsed??0)-512)*3,0,8400):8400;
        var result=await Retrieval.FindAsync(ProjectId,history[ix].Content,ct,budget);pendingSources=result.Sources;
        var context = result.Context;
        if (drafting) context += "\nWrite the requested document as Markdown, beginning with its title. Wizionic exports your answer to the chosen format.";
        history.Add(App.Core.Chat.ProjectTurnContext.Create(context, drafting));
        return history;
    }

    /// <summary>Export a finished Markdown draft to the selected format.</summary>
    private static Attachment ExportDraft(string markdown, string format, string title)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(title, @"[^\p{L}\p{N} _-]+", "").Trim();
        if (slug.Length == 0) slug = "Project report";
        switch (format)
        {
            case "md":
            {
                var b = System.Text.Encoding.UTF8.GetBytes(markdown);
                return new Attachment(slug + ".md", "text/markdown", Convert.ToBase64String(b), b.Length);
            }
            case "txt":
            {
                var plain = MarkdownToPlain(markdown);
                var b = System.Text.Encoding.UTF8.GetBytes(plain);
                return new Attachment(slug + ".txt", "text/plain", Convert.ToBase64String(b), b.Length);
            }
            case "html":
            {
                var body = Markdown.ToHtml(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
                var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + System.Net.WebUtility.HtmlEncode(title) +
                    "</title><style>body{font-family:system-ui,sans-serif;max-width:46rem;margin:2rem auto;line-height:1.6;padding:0 1rem}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.3rem .5rem}</style></head><body>" +
                    body + "</body></html>";
                var b = System.Text.Encoding.UTF8.GetBytes(html);
                return new Attachment(slug + ".html", "text/html", Convert.ToBase64String(b), b.Length);
            }
            default:
            {
                var rendered = MarkdownPdfService.Render(title, markdown, "a4");
                return new Attachment(slug + ".pdf", "application/pdf", Convert.ToBase64String(rendered.Bytes), rendered.Bytes.Length);
            }
        }
    }

    private static string MarkdownToPlain(string md)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(md, @"```[\s\S]*?```", m => m.Value.Trim('`', '\n'));
        t = System.Text.RegularExpressions.Regex.Replace(t, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\[([^\]]*)\]\([^)]*\)", "$1");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s{0,3}#{1,6}\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(\*\*|__|\*|_|`)", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^\s*[-*+]\s+", "- ", System.Text.RegularExpressions.RegexOptions.Multiline);
        return t.Trim();
    }

    async Task<ChatMessage> SaveProjectOutputs(ChatMessage message){
        if(ProjectId==null)return message;
        List<ProjectSource> outputs = message.ProjectOutputs?.ToList() ?? [];
        List<Attachment> failed=[];
        if (message.Attachments != null)
        foreach(var att in message.Attachments){try{
            var f=await Projects.AddFileAsync(ProjectId,att.Name,att.ContentType,Convert.FromBase64String(att.DataBase64),ProjectChatId,message.ItemId);
            outputs.Add(new(f.Id,f.Name,1));SyncService.ScheduleProjectSync();if(!_pageDisposed)await OnOutput.InvokeAsync(f);
        }catch(Exception ex){failed.Add(att);SetAttachStatus("Could not save project output: "+ex.Message,true);}}

        // Surface notes saved during this project chat as preview links in the transcript.
        try
        {
            var known = outputs.Select(o => o.FileId).ToHashSet();
            foreach (var row in (await Projects.IndexAsync()).Where(r => r.Id.StartsWith(ProjectId + ":n:") && !ProjectStore.Deleted(r)))
            {
                if (known.Contains(row.Id)) continue;
                var link = await Projects.ReadAsync<ProjectNoteLink>(row.Id);
                if (link?.ChatId != ProjectChatId) continue;
                outputs.Add(new ProjectSource(row.Id, link.Title, 0));
                known.Add(row.Id);
            }
        }
        catch { /* note links are optional */ }

        return message with {Attachments=failed.Count==0?null:failed,ProjectOutputs=outputs.Count>0?outputs:null};
    }

    /// <summary>After a UI "Add to notes" save in a project chat, link the note into the project.</summary>
    private async Task LinkProjectNote(string noteId, string? entryId, string title)
    {
        if (ProjectId == null) return;
        try
        {
            var linkId = ProjectId + ":n:" + Guid.NewGuid().ToString("N");
            await Projects.SaveAsync(linkId, title, new ProjectNoteLink(noteId, entryId, title, ProjectChatId));
            SyncService.ScheduleProjectSync();
            if (messages.Count > 0)
            {
                var last = messages[^1];
                var outputs = last.ProjectOutputs?.ToList() ?? [];
                if (!outputs.Any(o => o.FileId == linkId)) outputs.Add(new ProjectSource(linkId, title, 0));
                messages[^1] = last with { ProjectOutputs = outputs };
                await SaveCurrentConvo();
            }
        }
        catch { /* linking is best-effort */ }
    }

    private void OnProjectsChangedReconcile() => _ = InvokeAsync(ReconcileProjectNoteLinks);

    /// <summary>
    /// Attach note links saved in this chat (including earlier turns or another device)
    /// to the transcript so they render as clickable preview chips.
    /// </summary>
    private async Task ReconcileProjectNoteLinks()
    {
        if (ProjectId == null || _pageDisposed || isSending) return;
        try
        {
            var referenced = messages.Where(m => m.ProjectOutputs != null)
                .SelectMany(m => m.ProjectOutputs!).Select(o => o.FileId).ToHashSet();
            var toAdd = new List<ProjectSource>();
            foreach (var row in (await Projects.IndexAsync()).Where(r => r.Id.StartsWith(ProjectId + ":n:") && !ProjectStore.Deleted(r)))
            {
                if (referenced.Contains(row.Id)) continue;
                var link = await Projects.ReadAsync<ProjectNoteLink>(row.Id);
                if (link?.ChatId != ProjectChatId) continue;
                toAdd.Add(new ProjectSource(row.Id, link.Title, 0));
                referenced.Add(row.Id);
            }
            if (toAdd.Count == 0) return;
            var idx = messages.FindLastIndex(m => m.Role == "assistant");
            if (idx < 0) return;
            var msg = messages[idx];
            var outputs = msg.ProjectOutputs?.ToList() ?? [];
            outputs.AddRange(toAdd);
            messages[idx] = msg with { ProjectOutputs = outputs };
            await SaveCurrentConvo();
            await InvokeAsync(StateHasChanged);
        }
        catch { /* note chips are best-effort */ }
    }
}