using App.Core.Storage;

namespace App.Shared.Services;

/// <summary>Thread-safe one-shot handoff of a gallery image into Chat for AI edit.</summary>
public sealed class GalleryChatHandoff : IGalleryChatHandoff
{
    private readonly object _gate = new();
    private Attachment? _pending;

    public void SetEditWithAiRequest(Attachment image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate)
            _pending = image;
    }

    public bool TryTakeEditWithAiRequest(out Attachment? image)
    {
        lock (_gate)
        {
            image = _pending;
            _pending = null;
            return image != null && !string.IsNullOrWhiteSpace(image.DataBase64);
        }
    }
}
