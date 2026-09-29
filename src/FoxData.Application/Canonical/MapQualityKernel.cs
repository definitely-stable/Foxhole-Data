using System.Text.Json;
using FoxData.Core.Quality;

namespace FoxData.Application.Canonical;

public sealed class MapQualityKernel(IMapQualityStore store)
{
    public Task<MapQualityResult?> GetByRunIdAsync(
        MapQualityRunId runId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(runId.Value, nameof(runId));

        return store.GetByRunIdAsync(
            runId,
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

        if (write.CompletedAt < write.StartedAt)
        {
            throw new ArgumentException(
                "Quality completion must not be earlier than its start.",
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

        if (!Enum.IsDefined(finding.Effect))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finding.Effect),
                finding.Effect,
                "Map quality finding effect must be a defined value.");
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
                "A quality finding may reference either one map item occurrence or one map text occurrence, not both.",
                nameof(finding));
        }

        if (finding.DetailCode is { } detail)
        {
            ValidateRequiredText(
                detail,
                128,
                nameof(finding.DetailCode));
        }

        ValidateMetricsJson(finding.InputMetricsJson);
    }

    private static void ValidateMetricsJson(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Quality input metrics JSON must already be trimmed.",
                nameof(value));
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Quality input metrics JSON must be an object.",
                    nameof(value));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Quality input metrics JSON must be valid JSON.",
                nameof(value),
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
