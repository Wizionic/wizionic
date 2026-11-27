using App.Core.Storage;

namespace App.Core.ModelsDev;

/// <summary>
/// Applies models.dev metadata to saved model settings.
/// User overrides always win; existing capability flags are only replaced when
/// <c>overwrite</c> is true (the explicit "Sync metadata" action).
/// </summary>
public static class ModelsDevEnricher
{
    public static void Apply(CloudModelSettings model, ModelCatalogMetadata meta, bool overwrite)
    {
        if (!string.IsNullOrWhiteSpace(meta.ModelsDevId))
            model.ModelsDevId = meta.ModelsDevId;

        model.CatalogSource = "models.dev";

        if (meta.ContextSize > 0 && !model.UserOverrideContext &&
            (overwrite || model.ContextSize <= 0))
        {
            model.ContextSize = meta.ContextSize;
        }

        if (meta.MaxOutputTokens > 0)
            model.MaxOutputTokens = meta.MaxOutputTokens;

        model.SupportsReasoning = meta.SupportsReasoning ?? model.SupportsReasoning;
        model.KnowledgeCutoff = meta.KnowledgeCutoff ?? model.KnowledgeCutoff;
        model.ReleaseDate = meta.ReleaseDate ?? model.ReleaseDate;
        model.Family = meta.Family ?? model.Family;

        if (meta.CostInputPer1M.HasValue)
            model.CostInputPer1M = meta.CostInputPer1M;
        if (meta.CostOutputPer1M.HasValue)
            model.CostOutputPer1M = meta.CostOutputPer1M;
        if (meta.CostCacheReadPer1M.HasValue)
            model.CostCacheReadPer1M = meta.CostCacheReadPer1M;

        model.InputModalities = meta.InputModalities?.ToList() ?? model.InputModalities;
        model.OutputModalities = meta.OutputModalities?.ToList() ?? model.OutputModalities;

        if (meta.IsTts.HasValue && (overwrite || meta.IsTts == true))
            model.IsTts = meta.IsTts.Value;
        if (meta.IsTranscription.HasValue && (overwrite || meta.IsTranscription == true))
            model.IsTranscription = meta.IsTranscription.Value;
        if (meta.IsEmbeddings.HasValue && (overwrite || meta.IsEmbeddings == true))
            model.IsEmbeddings = meta.IsEmbeddings.Value;
        if (meta.IsReranking.HasValue && (overwrite || meta.IsReranking == true))
            model.IsReranking = meta.IsReranking.Value;

        if (!overwrite)
            return;

        if (!model.UserOverrideTools && meta.SupportsTools.HasValue)
            model.SupportsTools = meta.SupportsTools.Value;
        if (!model.UserOverrideVision && meta.SupportsVision.HasValue)
            model.SupportsVision = meta.SupportsVision.Value;
        if (!model.UserOverrideImage && meta.IsImage.HasValue)
            model.IsImage = meta.IsImage.Value;
        if (!model.UserOverrideEdit && meta.IsEdit.HasValue)
            model.IsEdit = meta.IsEdit.Value;
    }

    public static void ApplyOllama(OllamaModelSettings model, ModelCatalogMetadata meta, bool overwrite)
    {
        if (!string.IsNullOrWhiteSpace(meta.ModelsDevId))
            model.ModelsDevId = meta.ModelsDevId;

        if (meta.ContextSize > 0 && !model.UserOverrideContext &&
            (overwrite || model.ContextSize <= 0))
        {
            model.ContextSize = meta.ContextSize;
        }

        if (meta.MaxOutputTokens > 0)
            model.MaxOutputTokens = meta.MaxOutputTokens;
        model.SupportsReasoning = meta.SupportsReasoning ?? model.SupportsReasoning;
        model.KnowledgeCutoff = meta.KnowledgeCutoff ?? model.KnowledgeCutoff;
        model.ReleaseDate = meta.ReleaseDate ?? model.ReleaseDate;

        if (!overwrite)
            return;

        if (!model.UserOverrideTools && meta.SupportsTools.HasValue)
            model.SupportsTools = meta.SupportsTools.Value;
        if (!model.UserOverrideVision && meta.SupportsVision.HasValue)
            model.SupportsVision = meta.SupportsVision.Value;
    }

    public static void ApplyLemonade(LemonadeModelSettings model, ModelCatalogMetadata meta, bool overwrite)
    {
        if (!string.IsNullOrWhiteSpace(meta.ModelsDevId))
            model.ModelsDevId = meta.ModelsDevId;

        if (meta.ContextSize > 0 && !model.UserOverrideContext &&
            (overwrite || model.ContextSize <= 0))
        {
            model.ContextSize = meta.ContextSize;
        }

        if (meta.MaxOutputTokens > 0)
            model.MaxOutputTokens = meta.MaxOutputTokens;
        model.SupportsReasoning = meta.SupportsReasoning ?? model.SupportsReasoning;
        model.KnowledgeCutoff = meta.KnowledgeCutoff ?? model.KnowledgeCutoff;
        model.ReleaseDate = meta.ReleaseDate ?? model.ReleaseDate;

        if (!overwrite)
            return;

        if (!model.UserOverrideTools && meta.SupportsTools.HasValue)
            model.SupportsTools = meta.SupportsTools.Value;
        if (!model.UserOverrideVision && meta.SupportsVision.HasValue)
            model.SupportsVision = meta.SupportsVision.Value;
    }
}
