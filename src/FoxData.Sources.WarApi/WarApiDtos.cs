using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxData.Sources.WarApi;

public sealed class WarApiWarStateDto
{
    [JsonPropertyName("warId")]
    public string? WarId { get; set; }

    [JsonPropertyName("warNumber")]
    public int? WarNumber { get; set; }

    [JsonPropertyName("winner")]
    public string? Winner { get; set; }

    [JsonPropertyName("conquestStartTime")]
    public long? ConquestStartTime { get; set; }

    [JsonPropertyName("conquestEndTime")]
    public long? ConquestEndTime { get; set; }

    [JsonPropertyName("resistanceStartTime")]
    public long? ResistanceStartTime { get; set; }

    [JsonPropertyName("scheduledConquestEndTime")]
    public long? ScheduledConquestEndTime { get; set; }

    [JsonPropertyName("requiredVictoryTowns")]
    public int? RequiredVictoryTowns { get; set; }

    [JsonPropertyName("shortRequiredVictoryTowns")]
    public int? ShortRequiredVictoryTowns { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class WarApiWarReportDto
{
    [JsonPropertyName("totalEnlistments")]
    public long? TotalEnlistments { get; set; }

    [JsonPropertyName("colonialCasualties")]
    public long? ColonialCasualties { get; set; }

    [JsonPropertyName("wardenCasualties")]
    public long? WardenCasualties { get; set; }

    [JsonPropertyName("dayOfWar")]
    public int? DayOfWar { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class WarApiMapDataDto
{
    [JsonPropertyName("regionId")]
    public int? RegionId { get; set; }

    [JsonPropertyName("scorchedVictoryTowns")]
    public int? ScorchedVictoryTowns { get; set; }

    [JsonPropertyName("mapItems")]
    public WarApiMapItemDto[]? MapItems { get; set; }

    [JsonPropertyName("mapTextItems")]
    public WarApiMapTextItemDto[]? MapTextItems { get; set; }

    [JsonPropertyName("lastUpdated")]
    public long? LastUpdated { get; set; }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class WarApiMapItemDto
{
    [JsonPropertyName("teamId")]
    public string? TeamId { get; set; }

    [JsonPropertyName("iconType")]
    public int? IconType { get; set; }

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("flags")]
    public int? Flags { get; set; }

    [JsonPropertyName("viewDirection")]
    public int? ViewDirection { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class WarApiMapTextItemDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("mapMarkerType")]
    public string? MapMarkerType { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
