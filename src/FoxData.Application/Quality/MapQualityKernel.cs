using System.Text.Json;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;

namespace FoxData.Application.Quality;

public sealed class MapQualityKernel(IMapQualityStore store)
{
    public Task<MapQualityResult?> GetAsync(
        MapSnapshotId mapSnapshotId,
        WarRegionId warRegionId,
        FetchId validationFetchId,
        string taxonomyVersion,
        string qualityPolicyVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(mapSnapshotId.Value, nameof(mapSnapshotId));
        ValidateId(warRegionId.Value, nameof(warRegionId));
        ValidateId(validationFetchId.Value, nameof(validationFetchId));
        ValidateRequiredText(taxonomyVersion, 128, nameof(taxonomyVersion));
        ValidateRequiredText(
            qualityPolicyVersion,
            128,
            nameof(qualityPolicyVersion));

        return store.GetAsync(
            mapSnapshotId,
            warRegionId,
            validationFetchId,
            taxonomyVersion,
            qualityPolicyVersion,
            cancellationToken);
    }

    public Task<MapQualityResult> RecordAsync(
        MapQualityWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        ValidateId(write.MapSnapshotId.Value, nameof(write.MapSnapshotId));
        ValidateId(write.WarRegionId.Value, nameof(write.WarRegionId));
        ValidateId(
            write.ValidationFetchId.Value,
            nameof(write.ValidationFetchId));
        ValidateRequiredText(
            write.TaxonomyVersion,
            128,
            nameof(write.TaxonomyVersion));
        ValidateRequiredText(
            write.QualityPolicyVersion,
            128,
            nameof(write.QualityPolicyVersion));

        if (write.BaselineMapObservationId is { } baseline)
        {
            ValidateId(
                baseline.Value,
                nameof(write.BaselineMapObservationId));
        }

        if (!Enum.IsDefined(write.Decision))
        {
            throw new ArgumentOutOfRangeException(
                nameof(write.Decision),
                write.Decision,
                "Map quality decision must be a defined value.");
        }

        if (!Enum.IsDefined(write.Kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(write.Kind),
                write.Kind,
                "Map snapshot kind must be a defined value.");
        }

        if (write.CompletedAt < write.StartedAt)
        {
            throw new ArgumentException(
                "Quality completion must not be earlier than its start.",
                nameof(write));
        }

        ArgumentNullException.ThrowIfNull(write.Findings);
        for (var index = 0; index < write.Findings.Count; index++)
        {
            ValidateFinding(
                write.Findings[index]
                    ?? throw new ArgumentException(
                        "Map quality finding must not be null.",
                        nameof(write)),
                index);
        }

        return store.RecordAsync(write, cancellationToken);
    }

    private static void ValidateFinding(
        MapQualityFindingCandidate finding,
        int index)
    {
        ValidateRequiredText(
            finding.RuleKey,
            128,
            $"{nameof(MapQualityWrite.Findings)}[{index}].RuleKey");
        ValidateRequiredText(
            finding.RuleVersion,
            128,
            $"{nameof(MapQualityWrite.Findings)}[{index}].RuleVersion");
        ValidateRequiredText(
            finding.ConfigurationVersion,
            128,
            $"{nameof(MapQualityWrite.Findings)}[{index}].ConfigurationVersion");
        ValidateRequiredText(
            finding.Effect,
            32,
            $"{nameof(MapQualityWrite.Findings)}[{index}].Effect");

        if (finding.Effect is not (
                "informational" or
                "suspect" or
                "quarantined"))
        {
            throw new ArgumentException(
                $"Unsupported map quality finding effect '{finding.Effect}'.",
                nameof(finding));
        }

        if (finding.MapItemOccurrenceId is not null &&
            finding.MapTextOccurrenceId is not null)
        {
            throw new ArgumentException(
                "A quality finding may reference an item occurrence or a text occurrence, but not both.",
                nameof(finding));
        }

        if (finding.MapItemOccurrenceId is { } item)
        {
            ValidateId(item.Value, nameof(finding.MapItemOccurrenceId));
        }

        if (finding.MapTextOccurrenceId is { } text)
        {
            ValidateId(text.Value, nameof(finding.MapTextOccurrenceId));
        }

        ValidateOptionalText(
            finding.DetailCode,
            128,
            nameof(finding.DetailCode));

        if (string.IsNullOrWhiteSpace(finding.InputMetricsJson))
        {
            throw new ArgumentException(
                "Quality finding input metrics JSON must not be empty.",
                nameof(finding));
        }

        try
        {
            using var document =
                JsonDocument.Parse(finding.InputMetricsJson);

            if (document.RootElement.ValueKind !=
                JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Quality finding input metrics must be a JSON object.",
                    nameof(finding));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Quality finding input metrics must contain valid JSON.",
                nameof(finding),
                exception);
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

    private static void ValidateOptionalText(
        string? value,
        int maximum,
        string parameterName)
    {
        if (value is not null &&
            (string.IsNullOrWhiteSpace(value) ||
             value.Length > maximum ||
             !string.Equals(
                 value,
                 value.Trim(),
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"{parameterName} must be null or non-empty, already trimmed, and at most {maximum} characters.",
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
