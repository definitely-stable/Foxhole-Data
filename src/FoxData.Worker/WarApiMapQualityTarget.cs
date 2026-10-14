using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public sealed record WarApiMapQualityTarget
{
    private WarApiMapQualityTarget(
        string taxonomyVersion,
        string policyVersion)
    {
        TaxonomyVersion = taxonomyVersion;
        PolicyVersion = policyVersion;
    }

    public string TaxonomyVersion { get; }

    public string PolicyVersion { get; }

    public static WarApiMapQualityTarget Live =>
        ForPolicyVersion(WarApiVersions.MapQualityPolicy);

    public static WarApiMapQualityTarget ForPolicyVersion(
        string policyVersion)
    {
        var profile = WarApiMapQualityPolicyRegistry.Get(policyVersion);
        return new WarApiMapQualityTarget(
            profile.TaxonomyVersion,
            profile.Version);
    }

    public WarApiMapQualityPolicyProfile ResolveProfile()
    {
        var profile = WarApiMapQualityPolicyRegistry.Get(PolicyVersion);
        if (!string.Equals(
                profile.TaxonomyVersion,
                TaxonomyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Map quality target taxonomy does not match its registered policy profile.");
        }

        return profile;
    }
}
