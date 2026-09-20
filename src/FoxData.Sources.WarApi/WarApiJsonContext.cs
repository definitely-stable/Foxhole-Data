using System.Text.Json.Serialization;

namespace FoxData.Sources.WarApi;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WarApiWarStateDto))]
[JsonSerializable(typeof(WarApiWarReportDto))]
[JsonSerializable(typeof(WarApiMapDataDto))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WarApiJsonContext : JsonSerializerContext;
