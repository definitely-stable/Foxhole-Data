using System.Numerics;

namespace FoxData.Sources.WarApi;

public sealed record WarApiMapQualityFeatureItem(
    string? TeamId,
    int? IconType,
    double? X,
    double? Y,
    int? Flags,
    int? ViewDirection);

public sealed record WarApiMapQualityFeatureTextItem(
    string? Text,
    double? X,
    double? Y,
    string? MapMarkerType);

public sealed record WarApiMapQualityFeatureSnapshot(
    int? RegionId,
    long? SourceVersion,
    long? SourceLastUpdated,
    bool MapItemsPresent,
    bool MapTextItemsPresent,
    IReadOnlyList<WarApiMapQualityFeatureItem> Items,
    IReadOnlyList<WarApiMapQualityFeatureTextItem> TextItems)
{
    public static WarApiMapQualityFeatureSnapshot FromDto(
        WarApiMapDataDto source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WarApiMapQualityFeatureSnapshot(
            source.RegionId,
            source.Version,
            source.LastUpdated,
            source.MapItems is not null,
            source.MapTextItems is not null,
            source.MapItems?
                .Select(
                    item => new WarApiMapQualityFeatureItem(
                        item.TeamId,
                        item.IconType,
                        item.X,
                        item.Y,
                        item.Flags,
                        item.ViewDirection))
                .ToArray()
                ?? [],
            source.MapTextItems?
                .Select(
                    item => new WarApiMapQualityFeatureTextItem(
                        item.Text,
                        item.X,
                        item.Y,
                        item.MapMarkerType))
                .ToArray()
                ?? []);
    }
}

public sealed record WarApiMapQualityFeatureVector(
    bool HasBaseline,
    int? RegionId,
    int? BaselineRegionId,
    long? SourceVersion,
    long? BaselineSourceVersion,
    long? SourceLastUpdated,
    long? BaselineSourceLastUpdated,
    bool MapItemsPresent,
    bool MapTextItemsPresent,
    bool? BaselineMapItemsPresent,
    bool? BaselineMapTextItemsPresent,
    int ItemCount,
    int TextItemCount,
    int? BaselineItemCount,
    int? BaselineTextItemCount,
    int NoneCount,
    int WardenCount,
    int ColonialCount,
    int OtherTeamCount,
    int MissingTeamCount,
    int? BaselineNoneCount,
    int? BaselineWardenCount,
    int? BaselineColonialCount,
    int? BaselineOtherTeamCount,
    int? BaselineMissingTeamCount,
    double? NoneShare,
    double? BaselineNoneShare,
    double? NoneShareDelta,
    double? OwnedTeamShare,
    double? BaselineOwnedTeamShare,
    double? OwnedTeamShareDelta,
    double? ItemCountRatio,
    double? TextItemCountRatio,
    double? TotalOccurrenceCountRatio,
    int DistinctIconTypeCount,
    int? BaselineDistinctIconTypeCount,
    double? DistinctIconTypeRatio,
    double? DominantIconTypeShare,
    double? BaselineDominantIconTypeShare,
    double? IconConcentration,
    double? BaselineIconConcentration,
    double? IconConcentrationDelta,
    decimal? SourceVersionDelta,
    decimal? SourceLastUpdatedDeltaMilliseconds,
    int UnknownTeamCount,
    int UnknownIconCount,
    int UnknownFlagOccurrenceCount,
    int UnknownFlagBitCount,
    int InvalidCoordinateCount,
    bool? RegionIdChanged,
    int DuplicateItemGroupCount,
    int DuplicateItemExcessCount,
    int DuplicateTextGroupCount,
    int DuplicateTextExcessCount,
    IReadOnlyDictionary<int, int> IconTypeCounts,
    IReadOnlyDictionary<int, int>? BaselineIconTypeCounts);

public static class WarApiMapQualityFeatureExtractor
{
    public static WarApiMapQualityFeatureVector Extract(
        WarApiMapQualityFeatureSnapshot current,
        WarApiMapQualityFeatureSnapshot? baseline,
        WarApiMapTaxonomyInterpreter taxonomy)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(taxonomy);
        ArgumentNullException.ThrowIfNull(current.Items);
        ArgumentNullException.ThrowIfNull(current.TextItems);

        if (baseline is not null)
        {
            ArgumentNullException.ThrowIfNull(baseline.Items);
            ArgumentNullException.ThrowIfNull(baseline.TextItems);
        }

        var currentTeams = CountTeams(current.Items);
        TeamCounts? baselineTeams = baseline is null
            ? null
            : CountTeams(baseline.Items);
        var currentIcons = CountIcons(current.Items);
        var baselineIcons = baseline is null
            ? null
            : CountIcons(baseline.Items);

        var unknownTeamCount = 0;
        var unknownIconCount = 0;
        var unknownFlagOccurrences = 0;
        var unknownFlagBits = 0;

        foreach (var item in current.Items)
        {
            if (taxonomy.InterpretTeam(item.TeamId).Status ==
                WarApiMapTaxonomyLookupStatus.Unknown)
            {
                unknownTeamCount++;
            }

            if (taxonomy.InterpretIcon(item.IconType).Status ==
                WarApiMapTaxonomyLookupStatus.Unknown)
            {
                unknownIconCount++;
            }

            var flags = taxonomy.InterpretFlags(item.Flags);
            if (flags.Status ==
                WarApiMapFlagInterpretationStatus.ContainsUnknownBits)
            {
                unknownFlagOccurrences++;
                unknownFlagBits += BitOperations.PopCount(flags.UnknownBits);
            }
        }

        var currentNoneShare =
            Share(currentTeams.None, current.Items.Count);
        var baselineNoneShare = baselineTeams is null
            ? null
            : Share(baselineTeams.Value.None, baseline!.Items.Count);
        var currentOwnedShare =
            Share(
                currentTeams.Wardens + currentTeams.Colonials,
                current.Items.Count);
        var baselineOwnedShare = baselineTeams is null
            ? null
            : Share(
                baselineTeams.Value.Wardens +
                    baselineTeams.Value.Colonials,
                baseline!.Items.Count);

        var currentIconConcentration =
            IconConcentration(currentIcons);
        var baselineIconConcentration = baselineIcons is null
            ? null
            : IconConcentration(baselineIcons);

        var itemDuplicates = CountItemDuplicates(current.Items);
        var textDuplicates = CountTextDuplicates(current.TextItems);

        return new WarApiMapQualityFeatureVector(
            baseline is not null,
            current.RegionId,
            baseline?.RegionId,
            current.SourceVersion,
            baseline?.SourceVersion,
            current.SourceLastUpdated,
            baseline?.SourceLastUpdated,
            current.MapItemsPresent,
            current.MapTextItemsPresent,
            baseline?.MapItemsPresent,
            baseline?.MapTextItemsPresent,
            current.Items.Count,
            current.TextItems.Count,
            baseline?.Items.Count,
            baseline?.TextItems.Count,
            currentTeams.None,
            currentTeams.Wardens,
            currentTeams.Colonials,
            currentTeams.Other,
            currentTeams.Missing,
            baselineTeams?.None,
            baselineTeams?.Wardens,
            baselineTeams?.Colonials,
            baselineTeams?.Other,
            baselineTeams?.Missing,
            currentNoneShare,
            baselineNoneShare,
            Difference(currentNoneShare, baselineNoneShare),
            currentOwnedShare,
            baselineOwnedShare,
            Difference(currentOwnedShare, baselineOwnedShare),
            baseline is null
                ? null
                : Ratio(current.Items.Count, baseline.Items.Count),
            baseline is null
                ? null
                : Ratio(current.TextItems.Count, baseline.TextItems.Count),
            baseline is null
                ? null
                : Ratio(
                    current.Items.Count + current.TextItems.Count,
                    baseline.Items.Count + baseline.TextItems.Count),
            currentIcons.Count,
            baselineIcons?.Count,
            baselineIcons is null
                ? null
                : Ratio(currentIcons.Count, baselineIcons.Count),
            DominantShare(currentIcons),
            baselineIcons is null
                ? null
                : DominantShare(baselineIcons),
            currentIconConcentration,
            baselineIconConcentration,
            Difference(
                currentIconConcentration,
                baselineIconConcentration),
            Difference(
                current.SourceVersion,
                baseline?.SourceVersion),
            Difference(
                current.SourceLastUpdated,
                baseline?.SourceLastUpdated),
            unknownTeamCount,
            unknownIconCount,
            unknownFlagOccurrences,
            unknownFlagBits,
            CountInvalidCoordinates(current),
            baseline is null ||
                current.RegionId is null ||
                baseline.RegionId is null
                ? null
                : current.RegionId != baseline.RegionId,
            itemDuplicates.Groups,
            itemDuplicates.Excess,
            textDuplicates.Groups,
            textDuplicates.Excess,
            currentIcons,
            baselineIcons);
    }

    private static TeamCounts CountTeams(
        IReadOnlyList<WarApiMapQualityFeatureItem> items)
    {
        var none = 0;
        var wardens = 0;
        var colonials = 0;
        var other = 0;
        var missing = 0;

        foreach (var item in items)
        {
            switch (item.TeamId)
            {
                case null:
                    missing++;
                    break;
                case "NONE":
                    none++;
                    break;
                case "WARDENS":
                    wardens++;
                    break;
                case "COLONIALS":
                    colonials++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return new TeamCounts(
            none,
            wardens,
            colonials,
            other,
            missing);
    }

    private static SortedDictionary<int, int> CountIcons(
        IReadOnlyList<WarApiMapQualityFeatureItem> items)
    {
        var result = new SortedDictionary<int, int>();
        foreach (var item in items)
        {
            if (item.IconType is not { } iconType)
            {
                continue;
            }

            result.TryGetValue(iconType, out var count);
            result[iconType] = count + 1;
        }

        return result;
    }

    private static double? Share(int numerator, int denominator) =>
        denominator == 0
            ? null
            : numerator / (double)denominator;

    private static double? Ratio(int current, int baseline) =>
        baseline == 0
            ? null
            : current / (double)baseline;

    private static double? Difference(
        double? current,
        double? baseline) =>
        current is { } left && baseline is { } right
            ? left - right
            : null;

    private static decimal? Difference(
        long? current,
        long? baseline) =>
        current is { } left && baseline is { } right
            ? (decimal)left - right
            : null;

    private static double? DominantShare(
        IReadOnlyDictionary<int, int> counts)
    {
        var total = counts.Values.Sum();
        return total == 0
            ? null
            : counts.Values.Max() / (double)total;
    }

    private static double? IconConcentration(
        IReadOnlyDictionary<int, int> counts)
    {
        var total = counts.Values.Sum();
        if (total == 0)
        {
            return null;
        }

        var squaredShares = 0d;
        foreach (var count in counts.Values)
        {
            var share = count / (double)total;
            squaredShares += share * share;
        }

        return squaredShares;
    }

    private static int CountInvalidCoordinates(
        WarApiMapQualityFeatureSnapshot snapshot)
    {
        var count = 0;
        foreach (var item in snapshot.Items)
        {
            if (InvalidCoordinate(item.X) ||
                InvalidCoordinate(item.Y))
            {
                count++;
            }
        }

        foreach (var item in snapshot.TextItems)
        {
            if (InvalidCoordinate(item.X) ||
                InvalidCoordinate(item.Y))
            {
                count++;
            }
        }

        return count;
    }

    private static bool InvalidCoordinate(double? coordinate) =>
        coordinate is { } value &&
        (!double.IsFinite(value) || value is < 0d or > 1d);

    private static DuplicateCounts CountItemDuplicates(
        IReadOnlyList<WarApiMapQualityFeatureItem> items)
    {
        var groups = items
            .GroupBy(
                static item => new ItemIdentity(
                    item.TeamId,
                    item.IconType,
                    item.X,
                    item.Y,
                    item.Flags,
                    item.ViewDirection))
            .Select(static group => group.Count())
            .Where(static count => count > 1)
            .ToArray();

        return new DuplicateCounts(
            groups.Length,
            groups.Sum(static count => count - 1));
    }

    private static DuplicateCounts CountTextDuplicates(
        IReadOnlyList<WarApiMapQualityFeatureTextItem> items)
    {
        var groups = items
            .GroupBy(
                static item => new TextIdentity(
                    item.Text,
                    item.X,
                    item.Y,
                    item.MapMarkerType))
            .Select(static group => group.Count())
            .Where(static count => count > 1)
            .ToArray();

        return new DuplicateCounts(
            groups.Length,
            groups.Sum(static count => count - 1));
    }

    private readonly record struct TeamCounts(
        int None,
        int Wardens,
        int Colonials,
        int Other,
        int Missing);

    private readonly record struct DuplicateCounts(
        int Groups,
        int Excess);

    private readonly record struct ItemIdentity(
        string? TeamId,
        int? IconType,
        double? X,
        double? Y,
        int? Flags,
        int? ViewDirection);

    private readonly record struct TextIdentity(
        string? Text,
        double? X,
        double? Y,
        string? MapMarkerType);
}
