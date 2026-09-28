using FoxData.Core.Evidence;

namespace FoxData.Application.Canonical;

public sealed class MapSnapshotKernel(IMapSnapshotStore store)
{
    public Task<MapSnapshotResult?> GetByNormalizationRunAsync(
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(
            normalizationRunId.Value,
            nameof(normalizationRunId));

        return store.GetByNormalizationRunAsync(
            normalizationRunId,
            cancellationToken);
    }

    public Task<MapSnapshotResult> RecordAcceptedAsync(
        MapSnapshotWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        ValidateId(
            write.SourceParseRunId.Value,
            nameof(write.SourceParseRunId));
        ValidateId(
            write.RepresentationFetchId.Value,
            nameof(write.RepresentationFetchId));
        ValidateRequiredText(
            write.NormalizerVersion,
            128,
            nameof(write.NormalizerVersion));
        ValidateRequiredText(
            write.CapabilityKey,
            128,
            nameof(write.CapabilityKey));
        ValidateRequiredText(
            write.SemanticKey,
            256,
            nameof(write.SemanticKey));
        ValidateRequiredText(
            write.SourceMapName,
            256,
            nameof(write.SourceMapName));

        if (!Enum.IsDefined(write.Kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(write.Kind),
                write.Kind,
                "Map snapshot kind must be a defined value.");
        }

        if (write.NormalizationCompletedAt <
            write.NormalizationStartedAt)
        {
            throw new ArgumentException(
                "Normalization completion must not be earlier than its start.",
                nameof(write));
        }

        ArgumentNullException.ThrowIfNull(write.Items);
        ArgumentNullException.ThrowIfNull(write.TextItems);

        ValidateItemOrdinals(write.Items);
        ValidateTextOrdinals(write.TextItems);

        if (!write.SourceMapItemsArrayPresent &&
            write.Items.Count != 0)
        {
            throw new ArgumentException(
                "Map item occurrences cannot exist when the source mapItems array was null or absent.",
                nameof(write));
        }

        if (!write.SourceMapTextItemsArrayPresent &&
            write.TextItems.Count != 0)
        {
            throw new ArgumentException(
                "Map text occurrences cannot exist when the source mapTextItems array was null or absent.",
                nameof(write));
        }

        return store.RecordAcceptedAsync(
            write,
            cancellationToken);
    }

    private static void ValidateItemOrdinals(
        IReadOnlyList<MapItemOccurrenceCandidate> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var occurrence = items[index]
                ?? throw new ArgumentException(
                    "Map item occurrence must not be null.",
                    nameof(items));

            if (occurrence.SourceOrdinal != index)
            {
                throw new ArgumentException(
                    "Map item source ordinals must exactly match source array positions.",
                    nameof(items));
            }
        }
    }

    private static void ValidateTextOrdinals(
        IReadOnlyList<MapTextOccurrenceCandidate> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var occurrence = items[index]
                ?? throw new ArgumentException(
                    "Map text occurrence must not be null.",
                    nameof(items));

            if (occurrence.SourceOrdinal != index)
            {
                throw new ArgumentException(
                    "Map text source ordinals must exactly match source array positions.",
                    nameof(items));
            }
        }
    }

    private static void ValidateRequiredText(
        string value,
        int maximum,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum ||
            !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty, already trimmed, and at most {maximum} characters.",
                parameterName);
        }
    }

    private static void ValidateId(
        Guid value,
        string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Identifier must not be empty.",
                parameterName);
        }
    }
}
