namespace FoxData.Sources.WarApi;

public static class WarApiVersions
{
    public const string Adapter = "warapi-adapter@1";
    public const string Parser = "warapi-parser@1";
    public const string CachePolicy = "warapi-cache-policy@1";
    public const string PollPolicy = "warapi-poll@1";

    public static string SchedulingPolicy(
        WarApiCollectionProfile collectionProfile)
    {
        ArgumentNullException.ThrowIfNull(collectionProfile);
        return $"{PollPolicy}/{collectionProfile.Version}";
    }
}
