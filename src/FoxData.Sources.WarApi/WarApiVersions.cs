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
    public const string CoverageReprocessor = "warapi-coverage-reprocessor@1";

    public static string SchedulingPolicy(
        WarApiCollectionProfile collectionProfile)
    {
        ArgumentNullException.ThrowIfNull(collectionProfile);
        return $"{PollPolicy}/{collectionProfile.Version}";
    }
}
