using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxData.Sources.WarApi;

public sealed class WarApiWarStateDto
{
    [JsonPropertyName("warId")]
    public string? WarId { get; init; }

    [JsonPropertyName("warNumber")]
    public int? WarNumber { get; init; }

    [JsonPropertyName("winner")]
    public string? Winner { get; init; }

    [JsonPropertyName("conquestStartTime")]
    public long? ConquestStartTime { get; init; }

    [JsonPropertyName("conquestEndTime")]
    public long? ConquestEndTime { get; init; }

    [JsonPropertyName("resistanceStartTime")]
    public long? ResistanceStartTime { get; init; }

    [JsonPropertyName("scheduledConquestEndTime")]
    public long? ScheduledConquestEndTime { get; init; }

    [JsonPropertyName("requiredVictoryTowns")]
    public int? RequiredVictoryTowns { get; init; }

    [JsonPropertyName("shortRequiredVictoryTowns")]
    public int? ShortRequiredVictoryTowns { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class WarApiWarReportDto
{
    [JsonPropertyName("totalEnlistments")]
    public long? TotalEnlistments { get; init; }

    [JsonPropertyName("colonialCasualties")]
    public long? ColonialCasualties { get; init; }

    [JsonPropertyName("wardenCasualties")]
    public long? WardenCasualties { get; init; }

    [JsonPropertyName("dayOfWar")]
    public int? DayOfWar { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class WarApiMapDataDto
{
    [JsonPropertyName("regionId")]
    public int? RegionId { get; init; }

    [JsonPropertyName("scorchedVictoryTowns")]
    public int? ScorchedVictoryTowns { get; init; }

    [JsonPropertyName("mapItems")]
    public WarApiMapItemDto[]? MapItems { get; init; }

    [JsonPropertyName("mapTextItems")]
    public WarApiMapTextItemDto[]? MapTextItems { get; init; }

    [JsonPropertyName("lastUpdated")]
    public long? LastUpdated { get; init; }

    [JsonPropertyName("version")]
    public int? Version { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class WarApiMapItemDto
{
    [JsonPropertyName("teamId")]
    public string? TeamId { get; init; }

    [JsonPropertyName("iconType")]
    public int? IconType { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    [JsonPropertyName("flags")]
    public int? Flags { get; init; }

    [JsonPropertyName("viewDirection")]
    public int? ViewDirection { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class WarApiMapTextItemDto
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    [JsonPropertyName("mapMarkerType")]
    public string? MapMarkerType { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
