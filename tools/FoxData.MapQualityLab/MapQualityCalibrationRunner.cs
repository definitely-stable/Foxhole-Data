using System.Security.Cryptography;
using System.Text.Json;
using FoxData.Sources.WarApi;

namespace FoxData.MapQualityLab;

internal static class MapQualityCalibrationRunner
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
        };

    public static int Run(string[] args)
    {
        try
        {
            var options = Parse(args);
            var catalogPath = Path.GetFullPath(options.CatalogPath);
            var root = Path.GetDirectoryName(catalogPath)
                ?? throw new InvalidOperationException(
                    "Fixture catalog path has no parent directory.");
            var catalog = Read<FixtureCatalog>(catalogPath);
            var expectations =
                Read<CalibrationExpectations>(
                    Path.GetFullPath(options.ExpectationsPath));
            using var m4Document = JsonDocument.Parse(
                File.ReadAllText(
                    Path.GetFullPath(options.M4EvidencePath)));
            var m4Evidence = m4Document.RootElement.Clone();

            ValidateInputs(catalog, expectations, m4Evidence);
            ValidateRetainedM4Evidence(m4Evidence);
            var fixtures = catalog.Fixtures.ToDictionary(
                item => item.Id,
                StringComparer.Ordinal);
            ValidateCatalogRelationships(catalog, fixtures);
            var profile = WarApiMapQualityPolicyRegistry.Get(
                expectations.PolicyVersion);
            var taxonomy = new WarApiMapTaxonomyInterpreter(
                WarApiMapTaxonomyRegistry.Get(
                    profile.TaxonomyVersion));
            ValidateM4PolicyConstraints(profile);
            ValidateExpectationRules(expectations, profile);

            var cases = new List<CalibrationCase>();
            foreach (var expected in expectations.Cases
                         .OrderBy(
                             item => item.FixtureId,
                             StringComparer.Ordinal))
            {
                if (!fixtures.TryGetValue(
                        expected.FixtureId,
                        out var fixture))
                {
                    throw new InvalidOperationException(
                        $"Calibration expectation references unknown fixture '{expected.FixtureId}'.");
                }

                var current = Load(root, fixture);
                LoadedFixture? baseline = null;
                if (fixture.BaselineFixtureId is { } baselineId)
                {
                    if (!fixtures.TryGetValue(
                            baselineId,
                            out var baselineFixture))
                    {
                        throw new InvalidOperationException(
                            $"Fixture '{fixture.Id}' references unknown baseline '{baselineId}'.");
                    }

                    baseline = Load(root, baselineFixture);
                }

                var evaluation =
                    WarApiMapQualityPolicyEvaluator.Evaluate(
                        profile,
                        current.Snapshot,
                        baseline?.Snapshot,
                        baseline?.Snapshot.RegionId ??
                            current.Snapshot.RegionId,
                        current.StructuralFingerprint,
                        baseline?.StructuralFingerprint,
                        taxonomy);

                var actualDecision =
                    ToStorage(evaluation.Decision);
                var actualRuleKeys = evaluation.Findings
                    .Select(finding => finding.RuleKey)
                    .ToHashSet(StringComparer.Ordinal);
                var missingRequired = expected.RequiredRuleKeys
                    .Where(key => !actualRuleKeys.Contains(key))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var forbiddenPresent = expected.ForbiddenRuleKeys
                    .Where(actualRuleKeys.Contains)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var matched =
                    string.Equals(
                        expected.ExpectedDecision,
                        actualDecision,
                        StringComparison.Ordinal) &&
                    missingRequired.Length == 0 &&
                    forbiddenPresent.Length == 0;

                cases.Add(
                    new CalibrationCase(
                        fixture.Id,
                        fixture.Role,
                        fixture.ProvenanceKind,
                        fixture.BaselineFixtureId,
                        expected.ExpectedDecision,
                        actualDecision,
                        matched,
                        expected.FalsePositiveNote,
                        missingRequired,
                        forbiddenPresent,
                        current.PayloadSha256,
                        current.StructuralFingerprint,
                        baseline?.PayloadSha256,
                        baseline?.StructuralFingerprint,
                        evaluation.Features,
                        evaluation.Findings
                            .Select(
                                finding =>
                                    new CalibrationFinding(
                                        finding.RuleKey,
                                        finding.RuleVersion,
                                        finding.ConfigurationVersion,
                                        ToStorage(finding.Effect),
                                        finding.DetailCode,
                                        ParseMetrics(
                                            finding.InputMetricsJson)))
                            .ToArray()));
            }

            var report = new CalibrationReport(
                "m6-map-quality-calibration-report@1",
                expectations.ReviewedAt,
                profile.Version,
                profile.TaxonomyVersion,
                catalog.Version,
                expectations.Version,
                m4Evidence,
                new CalibrationSummary(
                    cases.Count,
                    cases.Count(item => item.Matched),
                    cases.Count(item => !item.Matched),
                    cases.Count(
                        item => item.ActualDecision == "accepted"),
                    cases.Count(
                        item => item.ActualDecision == "suspect"),
                    cases.Count(
                        item => item.ActualDecision == "quarantined")),
                cases);

            var json = JsonSerializer.Serialize(
                report,
                JsonOptions) + Environment.NewLine;
            if (options.OutputPath is null)
            {
                Console.Write(json);
            }
            else
            {
                var destination =
                    Path.GetFullPath(options.OutputPath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)
                    ?? Directory.GetCurrentDirectory());
                File.WriteAllText(destination, json);
            }

            return report.Summary.MismatchCount == 0
                ? 0
                : 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static LoadedFixture Load(
        string root,
        FixtureEntry fixture)
    {
        var capability = fixture.CapabilityKey switch
        {
            "dynamic-map-state" =>
                WarApiCapabilities.DynamicMapState,
            "static-map-state" =>
                WarApiCapabilities.StaticMapState,
            _ => throw new InvalidOperationException(
                $"Fixture '{fixture.Id}' has unsupported capability '{fixture.CapabilityKey}'."),
        };
        var bytes = File.ReadAllBytes(
            Path.Combine(root, fixture.File));
        var parsed = new WarApiParser().Parse(
            capability,
            bytes);
        if (!parsed.Parsed ||
            parsed.Value is not WarApiMapDataDto map ||
            parsed.StructuralFingerprint is null)
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.Id}' is not replayable map data.");
        }

        return new LoadedFixture(
            WarApiMapQualityFeatureSnapshot.FromDto(map),
            Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant(),
            parsed.StructuralFingerprint);
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(
            File.ReadAllText(path),
            JsonOptions)
        ?? throw new InvalidOperationException(
            $"Calibration input '{path}' deserialized to null.");

    private static void ValidateInputs(
        FixtureCatalog catalog,
        CalibrationExpectations expectations,
        JsonElement m4Evidence)
    {
        if (!string.Equals(
                catalog.Version,
                "m6-map-quality-calibration-fixtures@1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported fixture catalog '{catalog.Version}'.");
        }

        if (!string.Equals(
                expectations.Version,
                "m6-map-quality-calibration-expectations@1",
                StringComparison.Ordinal) ||
            !string.Equals(
                expectations.PolicyVersion,
                WarApiVersions.MapQualityPolicyV2,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Calibration expectations are not bound to policy@2.");
        }

        if (m4Evidence.ValueKind != JsonValueKind.Object ||
            m4Evidence.GetProperty("version").GetString() !=
                "m4-map-quality-calibration-evidence@1")
        {
            throw new InvalidOperationException(
                "Unsupported retained M4 calibration evidence.");
        }

        if (expectations.Cases.Count == 0)
        {
            throw new InvalidOperationException(
                "Calibration expectations must not be empty.");
        }

        if (catalog.Fixtures.Count == 0)
        {
            throw new InvalidOperationException(
                "Calibration fixture catalog must not be empty.");
        }

        var fixtureIds = catalog.Fixtures
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (fixtureIds.Count != catalog.Fixtures.Count)
        {
            throw new InvalidOperationException(
                "Calibration fixture catalog contains duplicate fixture IDs.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in expectations.Cases)
        {
            if (!ids.Add(item.FixtureId))
            {
                throw new InvalidOperationException(
                    $"Duplicate calibration expectation '{item.FixtureId}'.");
            }

            _ = ParseDecision(item.ExpectedDecision);
            if (string.IsNullOrWhiteSpace(item.FalsePositiveNote))
            {
                throw new InvalidOperationException(
                    $"Calibration expectation '{item.FixtureId}' requires a false-positive note.");
            }
        }

        if (!ids.SetEquals(fixtureIds))
        {
            var missingExpectations = fixtureIds
                .Except(ids, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            var missingFixtures = ids
                .Except(fixtureIds, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                "Calibration fixtures and expectations must have one-to-one coverage. " +
                $"Missing expectations: [{string.Join(", ", missingExpectations)}]; " +
                $"missing fixtures: [{string.Join(", ", missingFixtures)}].");
        }
    }

    private static void ValidateCatalogRelationships(
        FixtureCatalog catalog,
        IReadOnlyDictionary<string, FixtureEntry> fixtures)
    {
        foreach (var fixture in catalog.Fixtures)
        {
            if (string.IsNullOrWhiteSpace(fixture.Id) ||
                string.IsNullOrWhiteSpace(fixture.File) ||
                string.IsNullOrWhiteSpace(fixture.CapabilityKey) ||
                string.IsNullOrWhiteSpace(fixture.SourceMapName) ||
                string.IsNullOrWhiteSpace(fixture.Role) ||
                string.IsNullOrWhiteSpace(fixture.ProvenanceKind))
            {
                throw new InvalidOperationException(
                    "Calibration fixture metadata contains an empty required field.");
            }

            if (fixture.BaselineFixtureId is not { } baselineId)
            {
                continue;
            }

            if (!fixtures.TryGetValue(baselineId, out var baseline))
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixture.Id}' references unknown baseline '{baselineId}'.");
            }

            if (!string.Equals(
                    fixture.CapabilityKey,
                    baseline.CapabilityKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    fixture.SourceMapName,
                    baseline.SourceMapName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixture.Id}' baseline must use the same capability and source map identity.");
            }
        }
    }

    private static void ValidateExpectationRules(
        CalibrationExpectations expectations,
        WarApiMapQualityPolicyProfile profile)
    {
        var knownRules = profile.Rules
            .Select(rule => rule.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var expectation in expectations.Cases)
        {
            var required = expectation.RequiredRuleKeys
                .ToHashSet(StringComparer.Ordinal);
            var forbidden = expectation.ForbiddenRuleKeys
                .ToHashSet(StringComparer.Ordinal);

            if (required.Count != expectation.RequiredRuleKeys.Count ||
                forbidden.Count != expectation.ForbiddenRuleKeys.Count)
            {
                throw new InvalidOperationException(
                    $"Calibration expectation '{expectation.FixtureId}' contains duplicate rule keys.");
            }

            var overlap = required
                .Intersect(forbidden, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (overlap.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Calibration expectation '{expectation.FixtureId}' both requires and forbids: {string.Join(", ", overlap)}.");
            }

            var unknown = required
                .Concat(forbidden)
                .Where(key => !knownRules.Contains(key))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (unknown.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Calibration expectation '{expectation.FixtureId}' references unknown policy rules: {string.Join(", ", unknown)}.");
            }
        }
    }

    private static void ValidateRetainedM4Evidence(
        JsonElement m4Evidence)
    {
        var dynamic = m4Evidence.GetProperty(
            "dynamicMapState");

        var endpointCount =
            dynamic.GetProperty("endpointCount").GetInt32();
        var regressionCount =
            dynamic.GetProperty(
                "sourceVersionRegressionCount").GetInt32();
        var endpointsWithRegression =
            dynamic.GetProperty(
                "endpointsWithSourceVersionRegression").GetInt32();
        var gapCount =
            dynamic.GetProperty(
                "sourceVersionGapCount").GetInt32();
        var endpointsWithGap =
            dynamic.GetProperty(
                "endpointsWithSourceVersionGap").GetInt32();
        var lastUpdatedRegressions =
            dynamic.GetProperty(
                "sourceLastUpdatedRegressionCount").GetInt32();
        var unknownCodes =
            dynamic.GetProperty("unknownCodeCount").GetInt32();

        if (endpointCount <= 0 ||
            regressionCount <= 0 ||
            endpointsWithRegression <= 0 ||
            gapCount <= 0 ||
            endpointsWithGap <= 0 ||
            lastUpdatedRegressions != 0 ||
            unknownCodes <= 0)
        {
            throw new InvalidOperationException(
                "Retained M4 calibration evidence no longer supports the documented policy constraints.");
        }

        var source = m4Evidence.GetProperty("source");
        if (!string.Equals(
                source.GetProperty("artifactDigest").GetString(),
                "sha256:91411ee4ca339f2f56a945d9f8d4e235c598154d191c22b1a69ecc8a3b606e9e",
                StringComparison.Ordinal) ||
            !string.Equals(
                source.GetProperty(
                    "measurementSummarySha256").GetString(),
                "2cf939f8231cfe1a94c83dca7f7423d27f87dc9b6b4a215164abf683df2d1239",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Retained M4 calibration evidence digest does not match the reviewed source artifact.");
        }
    }

    private static void ValidateM4PolicyConstraints(
        WarApiMapQualityPolicyProfile profile)
    {
        var rules = profile.Rules.ToDictionary(
            rule => rule.Key,
            StringComparer.Ordinal);

        RequireEffect(
            rules,
            "source-version.regression",
            WarApiMapQualityEffect.Informational);
        RequireEffect(
            rules,
            "source-version.gap",
            WarApiMapQualityEffect.Informational);
        RequireEffect(
            rules,
            "source-last-updated.regression",
            WarApiMapQualityEffect.Suspect);
        RequireEffect(
            rules,
            "taxonomy.unknown-icon",
            WarApiMapQualityEffect.Informational);
        RequireEffect(
            rules,
            "taxonomy.unknown-team",
            WarApiMapQualityEffect.Informational);
        RequireEffect(
            rules,
            "taxonomy.unknown-flag-bits",
            WarApiMapQualityEffect.Informational);
    }

    private static void RequireEffect(
        IReadOnlyDictionary<string, WarApiMapQualityRuleProfile> rules,
        string key,
        WarApiMapQualityEffect expected)
    {
        if (!rules.TryGetValue(key, out var rule) ||
            rule.Effect != expected)
        {
            throw new InvalidOperationException(
                $"Policy@2 rule '{key}' violates retained M4 calibration constraints.");
        }
    }

    private static JsonElement ParseMetrics(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static WarApiMapQualityPolicyDecision ParseDecision(
        string value) =>
        value switch
        {
            "accepted" => WarApiMapQualityPolicyDecision.Accepted,
            "suspect" => WarApiMapQualityPolicyDecision.Suspect,
            "quarantined" =>
                WarApiMapQualityPolicyDecision.Quarantined,
            _ => throw new InvalidOperationException(
                $"Unsupported expected decision '{value}'."),
        };

    private static string ToStorage(
        WarApiMapQualityPolicyDecision decision) =>
        decision switch
        {
            WarApiMapQualityPolicyDecision.Accepted =>
                "accepted",
            WarApiMapQualityPolicyDecision.Suspect =>
                "suspect",
            WarApiMapQualityPolicyDecision.Quarantined =>
                "quarantined",
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision)),
        };

    private static string ToStorage(
        WarApiMapQualityEffect effect) =>
        effect switch
        {
            WarApiMapQualityEffect.Informational =>
                "informational",
            WarApiMapQualityEffect.Suspect =>
                "suspect",
            WarApiMapQualityEffect.Quarantined =>
                "quarantined",
            _ => throw new ArgumentOutOfRangeException(
                nameof(effect)),
        };

    private static CalibrationOptions Parse(string[] args)
    {
        var catalog =
            ".work/calibration/m6-map-quality/calibration-fixtures@1.json";
        var expectations =
            ".work/calibration/m6-map-quality/calibration-expectations@1.json";
        var m4Evidence =
            ".work/calibration/m6-map-quality/m4-calibration-evidence@1.json";
        string? output = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--catalog":
                    catalog = Value(args, ref index);
                    break;
                case "--expectations":
                    expectations = Value(args, ref index);
                    break;
                case "--m4-evidence":
                    m4Evidence = Value(args, ref index);
                    break;
                case "--output":
                    output = Value(args, ref index);
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "Usage: foxdata-map-quality-lab calibrate [--catalog <catalog.json>] [--expectations <expectations.json>] [--m4-evidence <m4.json>] [--output <report.json>]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown calibration argument '{args[index]}'.");
            }
        }

        return new CalibrationOptions(
            catalog,
            expectations,
            m4Evidence,
            output);
    }

    private static string Value(
        string[] args,
        ref int index)
    {
        if (++index >= args.Length ||
            string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException(
                "Calibration option requires a value.");
        }

        return args[index];
    }

    private sealed record CalibrationOptions(
        string CatalogPath,
        string ExpectationsPath,
        string M4EvidencePath,
        string? OutputPath);

    private sealed record FixtureCatalog(
        string Version,
        string ReviewedAt,
        IReadOnlyList<FixtureEntry> Fixtures);

    private sealed record FixtureEntry(
        string Id,
        string File,
        string CapabilityKey,
        string SourceMapName,
        string Role,
        string ProvenanceKind,
        string? SourceUrl,
        string? BaselineFixtureId,
        string Notes);

    private sealed record CalibrationExpectations(
        string Version,
        string ReviewedAt,
        string PolicyVersion,
        IReadOnlyList<CalibrationExpectation> Cases);

    private sealed record CalibrationExpectation(
        string FixtureId,
        string ExpectedDecision,
        IReadOnlyList<string> RequiredRuleKeys,
        IReadOnlyList<string> ForbiddenRuleKeys,
        string FalsePositiveNote);

    private sealed record LoadedFixture(
        WarApiMapQualityFeatureSnapshot Snapshot,
        string PayloadSha256,
        string StructuralFingerprint);

    private sealed record CalibrationFinding(
        string RuleKey,
        string RuleVersion,
        string ConfigurationVersion,
        string Effect,
        string DetailCode,
        JsonElement InputMetrics);

    private sealed record CalibrationCase(
        string FixtureId,
        string Role,
        string ProvenanceKind,
        string? BaselineFixtureId,
        string ExpectedDecision,
        string ActualDecision,
        bool Matched,
        string FalsePositiveNote,
        IReadOnlyList<string> MissingRequiredRuleKeys,
        IReadOnlyList<string> ForbiddenRuleKeysPresent,
        string PayloadSha256,
        string StructuralFingerprint,
        string? BaselinePayloadSha256,
        string? BaselineStructuralFingerprint,
        WarApiMapQualityFeatureVector Features,
        IReadOnlyList<CalibrationFinding> Findings);

    private sealed record CalibrationSummary(
        int CaseCount,
        int MatchedCount,
        int MismatchCount,
        int AcceptedCount,
        int SuspectCount,
        int QuarantinedCount);

    private sealed record CalibrationReport(
        string Version,
        string ReviewedAt,
        string PolicyVersion,
        string TaxonomyVersion,
        string FixtureCatalogVersion,
        string ExpectationsVersion,
        JsonElement M4Evidence,
        CalibrationSummary Summary,
        IReadOnlyList<CalibrationCase> Cases);
}
