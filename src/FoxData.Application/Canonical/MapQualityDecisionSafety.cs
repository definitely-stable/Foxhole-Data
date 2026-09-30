namespace FoxData.Application.Canonical;

// Source-neutral safety boundary: a caller may choose a more conservative
// decision, but cannot silently downgrade a finding's blocking severity.
public static class MapQualityDecisionSafety
{
    public static void Validate(MapQualityWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Findings);

        foreach (var finding in write.Findings)
        {
            ArgumentNullException.ThrowIfNull(finding);
            if (write.Decision == MapQualityDecision.Accepted &&
                finding.Effect is (
                    MapQualityFindingEffect.Suspect or
                    MapQualityFindingEffect.Quarantined))
            {
                throw new ArgumentException(
                    "Accepted map quality may not contain suspect or quarantined findings.",
                    nameof(write));
            }

            if (write.Decision == MapQualityDecision.Suspect &&
                finding.Effect == MapQualityFindingEffect.Quarantined)
            {
                throw new ArgumentException(
                    "Suspect map quality may not downgrade a quarantined finding.",
                    nameof(write));
            }
        }
    }
}
