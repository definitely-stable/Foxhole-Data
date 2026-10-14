namespace FoxData.Sources.WarApi;

public static class WarApiVersions
{
    public const string Adapter = "warapi-adapter@1";
    public const string Parser = "warapi-parser@1";
    public const string CachePolicy = "warapi-cache-policy@1";
    public const string BackoffPolicy = "warapi-backoff@1";
    public const string PollPolicy = "warapi-poll@1";
    public const string WarNormalizer = "warapi-war-normalizer@1";
    public const string RegionNormalizer = "warapi-region-normalizer@1";
    public const string WarReportNormalizer = "warapi-war-report-normalizer@1";
    public const string StaticMapNormalizer = "warapi-static-map-normalizer@1";
    public const string DynamicMapNormalizer = "warapi-dynamic-map-normalizer@1";
    public const string MapTaxonomy = "warapi-map-taxonomy@1";
    public const string MapQualityPolicyV1 = "warapi-map-quality@1";
    public const string MapQualityPolicyV2 = "warapi-map-quality@2";

    // M6-G8A/B/C proved evaluator parity, explicit version targeting and
    // ordered policy@2 rebuild over durable evidence. G8-D activates the
    // calibrated policy while older @1 results remain immutable/replayable.
    public const string MapQualityPolicy = MapQualityPolicyV2;
    public const string LatestMapQualityPolicy = MapQualityPolicyV2;
    public const string CoverageReprocessor = "warapi-coverage-reprocessor@1";

    public static string SchedulingPolicy(
        WarApiCollectionProfile collectionProfile)
    {
        ArgumentNullException.ThrowIfNull(collectionProfile);
        return $"{PollPolicy}/{collectionProfile.Version}";
    }
}
