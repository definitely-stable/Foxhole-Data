using System.Text.Json;
using FoxData.Core.Evidence;
using FoxData.Core.Quality;
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
        ValidateRequiredText(
            taxonomyVersion,
            128,
            nameof(taxonomyVersion));
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

        ValidateId(
            write.MapSnapshotId.Value,
            nameof(write.MapSnapshotId));
        ValidateId(
            write.WarRegionId.Value,
            nameof(write.WarRegionId));
        ValidateId(
            write.ValidationFetchId.Value,
            nameof(write.ValidationFetchId));

        if (write.BaselineMapObservationId is { } baseline)
        {
            ValidateId(
                baseline.Value,
                nameof(write.BaselineMapObservationId));
        }

        ValidateRequiredText(
            write.TaxonomyVersion,
            128,
            nameof(write.TaxonomyVersion));
        ValidateRequiredText(
            write.QualityPolicyVersion,
            128,
            nameof(write.QualityPolicyVersion));

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
                "Quality evaluation completion must not precede its start.",
                nameof(write));
        }

        ArgumentNullException.ThrowIfNull(write.Findings);
        foreach (var finding in write.Findings)
        {
            ValidateFinding(finding);
        }

        return store.RecordAsync(
            write,
            cancellationToken);
    }

    private static void ValidateFinding(
        MapQualityFindingCandidate finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        ValidateRequiredText(
            finding.RuleKey,
            128,
            nameof(finding.RuleKey));
        ValidateRequiredText(
            finding.RuleVersion,
            128,
            nameof(finding.RuleVersion));
        ValidateRequiredText(
            finding.ConfigurationVersion,
            128,
            nameof(finding.ConfigurationVersion));
        ValidateRequiredText(
            finding.Effect,
            32,
            nameof(finding.Effect));

        if (finding.Effect is not (
                "informational" or
                "suspect" or
                "quarantined"))
        {
            throw new ArgumentException(
                $"Unsupported map quality finding effect '{finding.Effect}'.",
                nameof(finding));
        }

        if (finding.MapItemOccurrenceId is { } item)
        {
            ValidateId(
                item.Value,
                nameof(finding.MapItemOccurrenceId));
        }

        if (finding.MapTextOccurrenceId is { } text)
        {
            ValidateId(
                text.Value,
                nameof(finding.MapTextOccurrenceId));
        }

        if (finding.MapItemOccurrenceId is not null &&
            finding.MapTextOccurrenceId is not null)
        {
            throw new ArgumentException(
                "A quality finding may reference an item occurrence or a text occurrence, but not both.",
                nameof(finding));
        }

        ValidateOptionalText(
            finding.DetailCode,
            128,
            nameof(finding.DetailCode));
        ValidateMetricsJson(finding.InputMetricsJson);
    }

    private static void ValidateMetricsJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Quality finding input metrics JSON must not be empty.",
                nameof(value));
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Quality finding input metrics JSON must be an object.",
                    nameof(value));
            }

            ValidateNoDuplicateObjectProperties(
                document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Quality finding input metrics JSON must be valid JSON.",
                nameof(value),
                exception);
        }
    }

    private static void ValidateNoDuplicateObjectProperties(
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(
                    StringComparer.Ordinal);
                foreach (var property in
                         element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new ArgumentException(
                            $"Quality finding input metrics JSON contains duplicate property '{property.Name}'.");
                    }

                    ValidateNoDuplicateObjectProperties(
                        property.Value);
                }

                break;
            }

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ValidateNoDuplicateObjectProperties(item);
                }

                break;
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
