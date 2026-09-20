using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FoxData.Application.Sources;
using FoxData.Infrastructure.Sources;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;
using Npgsql;

namespace FoxData.SourceMeasurement;

internal static class MeasurementRunner
{
    private const string MeasurementVersion = "m4-measurement@1";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 ||
                args[0] is "--help" or "-h" or "help")
            {
                WriteHelp();
                return 0;
            }

            return args[0] switch
            {
                "analyze" => await AnalyzeAsync(
                    ParseAnalyzeOptions(args[1..]),
                    CancellationToken.None),
                "storage" => await CaptureStorageAsync(
                    ParseStorageOptions(args[1..]),
                    CancellationToken.None),
                "validate" => await ValidateAsync(
                    ParseValidationOptions(args[1..]),
                    CancellationToken.None),
                _ => Fail(
                    $"Unknown command '{args[0]}'. Use --help for usage."),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Measurement operation was cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> AnalyzeAsync(
        AnalyzeOptions options,
        CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);

        var reader = new PostgresSourceMeasurementReader(dataSource);

        var fetches = new List<SourceMeasurementFetch>();
        await foreach (var fetch in reader.ReadFetchesAsync(
            WarApiCatalog.SourceKey,
            options.StartInclusive,
            options.EndExclusive,
            cancellationToken))
        {
            fetches.Add(fetch);
        }

        if (fetches.Count == 0)
        {
            throw new InvalidOperationException(
                "The selected measurement window contains no official War API fetches.");
        }

        var attempts = new List<SourceMeasurementAttempt>();
        await foreach (var attempt in reader.ReadAttemptsAsync(
            WarApiCatalog.SourceKey,
            options.StartInclusive,
            options.EndExclusive,
            cancellationToken))
        {
            attempts.Add(attempt);
        }

        var parseRuns = new List<SourceMeasurementParseRun>();
        await foreach (var parseRun in reader.ReadParseRunsAsync(
            WarApiCatalog.SourceKey,
            options.StartInclusive,
            options.EndExclusive,
            cancellationToken))
        {
            if (string.Equals(
                    parseRun.AdapterVersion,
                    WarApiVersions.Adapter,
                    StringComparison.Ordinal) &&
                string.Equals(
                    parseRun.ParserVersion,
                    WarApiVersions.Parser,
                    StringComparison.Ordinal))
            {
                parseRuns.Add(parseRun);
            }
        }

        var scheduleDecisions =
            new List<SourceMeasurementScheduleDecision>();
        await foreach (var decision in reader.ReadScheduleDecisionsAsync(
            WarApiCatalog.SourceKey,
            options.StartInclusive,
            options.EndExclusive,
            cancellationToken))
        {
            scheduleDecisions.Add(decision);
        }

        var probeManifest = BuildProbeManifest(
            options,
            scheduleDecisions);

        var parseByFetch = parseRuns.ToDictionary(
            parseRun => parseRun.RepresentationFetchId.Value);

        var series = fetches
            .GroupBy(
                fetch => new
                {
                    fetch.ShardKey,
                    fetch.SemanticKey,
                    fetch.CapabilityKey,
                })
            .Select(group =>
            {
                var capability =
                    CapabilityFromKey(group.Key.CapabilityKey);
                var endpointFetches = group
                    .OrderBy(fetch => fetch.RequestStartedAt)
                    .ThenBy(fetch => fetch.FetchId.Value)
                    .Select(fetch =>
                    {
                        _ = parseByFetch.TryGetValue(
                            fetch.FetchId.Value,
                            out var parseRun);

                        return new WarApiMeasurementSample(
                            group.Key.SemanticKey,
                            capability,
                            fetch.RequestStartedAt,
                            fetch.StatusCode,
                            fetch.PayloadSha256Hex,
                            fetch.PayloadBytes,
                            fetch.SourceEtag,
                            fetch.DurationMs,
                            parseRun?.SourceVersion,
                            fetch.StatusCode == 304 &&
                                fetch.PriorFetchId is not null);
                    })
                    .ToArray();

                var endpointParseRuns = parseRuns
                    .Where(
                        parseRun =>
                            string.Equals(
                                parseRun.ShardKey,
                                group.Key.ShardKey,
                                StringComparison.Ordinal) &&
                            string.Equals(
                                parseRun.SemanticKey,
                                group.Key.SemanticKey,
                                StringComparison.Ordinal) &&
                            string.Equals(
                                parseRun.CapabilityKey,
                                group.Key.CapabilityKey,
                                StringComparison.Ordinal))
                    .OrderBy(
                        parseRun =>
                            parseRun.RepresentationObservedAt)
                    .ThenBy(parseRun => parseRun.ParseRunId.Value)
                    .Select(
                        parseRun =>
                            new WarApiParseMeasurementSample(
                                group.Key.SemanticKey,
                                capability,
                                parseRun.RepresentationObservedAt,
                                parseRun.Outcome,
                                parseRun.StructuralFingerprint,
                                parseRun.UnknownPropertyCount,
                                parseRun.UnknownCodeCount,
                                parseRun.SourceVersion,
                                parseRun.SourceLastUpdated))
                    .ToArray();

                return new WarApiMeasurementSeries(
                    group.Key.ShardKey,
                    group.Key.SemanticKey,
                    capability,
                    endpointFetches,
                    endpointParseRuns);
            })
            .ToArray();

        var warApiReport =
            WarApiMeasurementReportBuilder.Build(series);

        var storageGrowth = await TryReadStorageGrowthAsync(
            options.OutputDirectory,
            cancellationToken);
        var scheduling = AnalyzeScheduling(
            fetches,
            attempts,
            scheduleDecisions);
        var downsampling = BuildDownsampling(
            fetches,
            attempts,
            scheduleDecisions,
            parseByFetch);

        var summary = new MeasurementSummary(
            options.RunId,
            WarApiCatalog.SourceKey,
            options.StartInclusive,
            options.EndExclusive,
            fetches.Count,
            attempts.Count,
            parseRuns.Count,
            series.Length,
            fetches.Count(fetch => fetch.PayloadSha256Hex is not null),
            fetches
                .Where(fetch => fetch.PayloadSha256Hex is not null)
                .Select(fetch => fetch.PayloadSha256Hex!)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            PayloadDeduplicationRatio(fetches),
            fetches.Count(
                fetch =>
                    fetch.DeclaredLength is { } declared &&
                    fetch.PayloadBytes is { } captured &&
                    declared != captured),
            fetches.Count(
                fetch =>
                    !string.IsNullOrWhiteSpace(fetch.SourceEtag)),
            CountBy(
                fetches,
                fetch =>
                    fetch.StatusCode is { } status
                        ? status.ToString(CultureInfo.InvariantCulture)
                        : "transport-null"),
            CountBy(
                fetches,
                fetch =>
                    string.IsNullOrWhiteSpace(fetch.ContentEncoding)
                        ? "identity"
                        : fetch.ContentEncoding!),
            AnalyzeEtags(
                fetches,
                warApiReport),
            AnalyzePayloadDecode(
                fetches,
                parseRuns),
            AnalyzeLatencyByResponseClass(fetches),
            AnalyzeVolume(
                options,
                fetches,
                parseRuns,
                scheduleDecisions),
            AnalyzeExecutorCapacity(
                fetches,
                scheduleDecisions),
            AnalyzeCache(fetches),
            AnalyzeAttempts(attempts),
            scheduling,
            downsampling,
            storageGrowth,
            warApiReport);

        var generatedAt = DateTimeOffset.UtcNow;
        var manifest = new MeasurementManifest(
            MeasurementVersion,
            options.RunId,
            WarApiCatalog.SourceKey,
            options.StartInclusive,
            options.EndExclusive,
            generatedAt,
            options.RepositorySha,
            options.ObserverRegion,
            WarApiVersions.Adapter,
            WarApiVersions.Parser,
            WarApiVersions.CachePolicy,
            WarApiVersions.BackoffPolicy,
            WarApiVersions.PollPolicy,
            options.CollectionProfileVersion,
            fetches
                .Select(fetch => fetch.ShardKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            probeManifest,
            CountBy(
                scheduleDecisions,
                decision => decision.PolicyVersion),
            [
                "Observations bound source state to retrieval times; they do not prove exact upstream event times.",
                "Counterfactual cadence downsampling does not synthesize HTTP 200/304 validator behaviour.",
                "ingest.collection_jobs.available_at is mutable across deferral/requeue and is not treated as immutable per-attempt history.",
            ]);

        Directory.CreateDirectory(options.OutputDirectory);

        await WriteJsonAsync(
            Path.Combine(
                options.OutputDirectory,
                "measurement-manifest.json"),
            manifest,
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(
                options.OutputDirectory,
                "measurement-summary.json"),
            summary,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(
                options.OutputDirectory,
                "measurement-report.md"),
            RenderMarkdown(manifest, summary),
            Encoding.UTF8,
            cancellationToken);

        Console.WriteLine(
            $"Wrote M4 measurement outputs to '{options.OutputDirectory}'.");
        return 0;
    }

    private static async Task<int> CaptureStorageAsync(
        StorageOptions options,
        CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);

        var reader =
            new PostgresSourceMeasurementStorageReader(dataSource);
        var snapshot = await reader.ReadAsync(cancellationToken);

        Directory.CreateDirectory(options.OutputDirectory);
        var path = Path.Combine(
            options.OutputDirectory,
            $"storage-{options.Label}.json");

        await WriteJsonAsync(
            path,
            snapshot,
            cancellationToken);

        Console.WriteLine($"Wrote PostgreSQL storage snapshot to '{path}'.");
        return 0;
    }

    private static MeasurementEtagSummary AnalyzeEtags(
        IReadOnlyCollection<SourceMeasurementFetch> fetches,
        WarApiMeasurementReport report)
    {
        var present = 0;
        var strong = 0;
        var weak = 0;
        var unusable = 0;

        foreach (var fetch in fetches)
        {
            if (string.IsNullOrWhiteSpace(fetch.SourceEtag))
            {
                continue;
            }

            present++;
            var usable =
                WarApiRequestBuilder.UsableValidator(
                    fetch.SourceEtag);

            if (usable is null)
            {
                unusable++;
            }
            else if (usable.StartsWith(
                         "W/",
                         StringComparison.OrdinalIgnoreCase))
            {
                weak++;
            }
            else
            {
                strong++;
            }
        }

        return new MeasurementEtagSummary(
            present,
            strong,
            weak,
            unusable,
            report.Endpoints.Sum(
                endpoint =>
                    endpoint.Fetches
                        .SameEtagDifferentPayloadCount),
            report.Endpoints.Sum(
                endpoint =>
                    endpoint.Fetches
                        .DifferentEtagSamePayloadCount));
    }

    private static MeasurementPayloadDecodeSummary AnalyzePayloadDecode(
        IReadOnlyCollection<SourceMeasurementFetch> fetches,
        IReadOnlyCollection<SourceMeasurementParseRun> parseRuns)
    {
        var fetchById = fetches.ToDictionary(
            fetch => fetch.FetchId.Value);

        var decodedSizes = new List<double>();
        var expansionRatios = new List<double>();
        var decodedCount = 0;
        var missingDecodedLengthCount = 0;

        foreach (var parseRun in parseRuns)
        {
            if (parseRun.DecodedByteLength is not { } decodedLength)
            {
                missingDecodedLengthCount++;
                continue;
            }

            decodedCount++;
            decodedSizes.Add(decodedLength);

            if (fetchById.TryGetValue(
                    parseRun.RepresentationFetchId.Value,
                    out var fetch) &&
                fetch.PayloadBytes is > 0)
            {
                expansionRatios.Add(
                    (double)decodedLength /
                    fetch.PayloadBytes.Value);
            }
        }

        return new MeasurementPayloadDecodeSummary(
            parseRuns.Count,
            decodedCount,
            missingDecodedLengthCount,
            Percentiles(decodedSizes),
            Percentiles(expansionRatios));
    }

    private static IReadOnlyDictionary<string, MeasurementPercentiles>
        AnalyzeLatencyByResponseClass(
            IReadOnlyCollection<SourceMeasurementFetch> fetches)
    {
        var result =
            new SortedDictionary<string, MeasurementPercentiles>(
                StringComparer.Ordinal);

        foreach (var group in fetches.GroupBy(
                     fetch => ResponseClass(fetch.StatusCode),
                     StringComparer.Ordinal))
        {
            result[group.Key] = Percentiles(
                group.Select(fetch => (double)fetch.DurationMs));
        }

        return result;
    }

    private static MeasurementVolumeSummary AnalyzeVolume(
        AnalyzeOptions options,
        IReadOnlyCollection<SourceMeasurementFetch> fetches,
        IReadOnlyCollection<SourceMeasurementParseRun> parseRuns,
        IReadOnlyCollection<SourceMeasurementScheduleDecision> decisions)
    {
        var days =
            (options.EndExclusive - options.StartInclusive)
            .TotalDays;

        if (days <= 0)
        {
            throw new InvalidOperationException(
                "Measurement window duration must be positive.");
        }

        var createdPayloadRows = fetches
            .Where(
                fetch =>
                    fetch.PayloadId is not null &&
                    fetch.PayloadCreatedAt is { } createdAt &&
                    createdAt >= options.StartInclusive &&
                    createdAt < options.EndExclusive)
            .Select(fetch => fetch.PayloadId!.Value.Value)
            .Distinct()
            .Count();

        var scheduleRows = decisions.Count(
            decision =>
                decision.CreatedAt >= options.StartInclusive &&
                decision.CreatedAt < options.EndExclusive);

        return new MeasurementVolumeSummary(
            days,
            fetches.Count / days,
            createdPayloadRows / days,
            parseRuns.Count / days,
            scheduleRows / days);
    }

    private static MeasurementExecutorCapacitySummary AnalyzeExecutorCapacity(
        IReadOnlyCollection<SourceMeasurementFetch> fetches,
        IReadOnlyCollection<SourceMeasurementScheduleDecision> decisions)
    {
        var windowFetchIds = new HashSet<Guid>(
            fetches.Select(fetch => fetch.FetchId.Value));

        var cadenceByEndpoint = decisions
            .Where(
                decision =>
                    decision.EndpointActive &&
                    windowFetchIds.Contains(
                        decision.FetchId.Value))
            .GroupBy(decision => decision.EndpointId.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Min(
                    decision =>
                        decision.EffectiveCadenceMs));

        var endpointLoads = fetches
            .GroupBy(fetch => fetch.EndpointId.Value)
            .Select(group =>
            {
                if (!cadenceByEndpoint.TryGetValue(
                        group.Key,
                        out var cadenceMs))
                {
                    return null;
                }

                var first = group.First();
                var p95DurationMs = Percentiles(
                    group.Select(
                        fetch => (double)fetch.DurationMs))
                    .P95 ?? 0;

                return new
                {
                    first.ShardKey,
                    first.CapabilityKey,
                    Load = p95DurationMs / cadenceMs,
                };
            })
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();

        var groups = endpointLoads
            .GroupBy(
                value =>
                    (value.ShardKey, value.CapabilityKey))
            .Select(
                group =>
                    new MeasurementExecutorCapabilityLoad(
                        group.Key.ShardKey,
                        group.Key.CapabilityKey,
                        group.Count(),
                        group.Sum(value => value.Load)))
            .OrderBy(
                group => group.ShardKey,
                StringComparer.Ordinal)
            .ThenBy(
                group => group.CapabilityKey,
                StringComparer.Ordinal)
            .ToArray();

        var totalLoad =
            groups.Sum(group => group.RequiredSerialServiceLoad);

        return new MeasurementExecutorCapacitySummary(
            ExecutorConcurrency: 1,
            ModeledEndpointCount: endpointLoads.Length,
            RequiredSerialServiceLoad: totalLoad,
            RemainingSerialHeadroom: 1d - totalLoad,
            MinimumModeledConcurrency: Math.Max(
                1,
                checked((int)Math.Ceiling(totalLoad))),
            Groups: groups);
    }

    private static string ResponseClass(int? statusCode) =>
        statusCode switch
        {
            null => "transport-null",
            >= 100 and < 200 => "1xx",
            >= 200 and < 300 => "2xx",
            >= 300 and < 400 => "3xx",
            >= 400 and < 500 => "4xx",
            >= 500 and < 600 => "5xx",
            _ => "other",
        };

    private static async Task<int> ValidateAsync(
        ValidationOptions options,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(
            options.OutputDirectory,
            "measurement-manifest.json");
        var summaryPath = Path.Combine(
            options.OutputDirectory,
            "measurement-summary.json");

        if (!File.Exists(manifestPath) ||
            !File.Exists(summaryPath))
        {
            throw new InvalidOperationException(
                "measurement-manifest.json and measurement-summary.json must both exist before validation.");
        }

        var manifest = JsonSerializer.Deserialize<MeasurementManifest>(
            await File.ReadAllTextAsync(
                manifestPath,
                cancellationToken),
            JsonOptions)
            ?? throw new InvalidOperationException(
                "measurement-manifest.json could not be deserialized.");
        var summary = JsonSerializer.Deserialize<MeasurementSummary>(
            await File.ReadAllTextAsync(
                summaryPath,
                cancellationToken),
            JsonOptions)
            ?? throw new InvalidOperationException(
                "measurement-summary.json could not be deserialized.");

        var errors = new List<string>();
        var warnings = new List<string>();

        if (!string.Equals(
                manifest.RunId,
                summary.RunId,
                StringComparison.Ordinal) ||
            manifest.StartInclusive != summary.StartInclusive ||
            manifest.EndExclusive != summary.EndExclusive ||
            !string.Equals(
                manifest.SourceKey,
                summary.SourceKey,
                StringComparison.Ordinal))
        {
            errors.Add(
                "Manifest and summary identity/window do not match.");
        }

        var duration =
            manifest.EndExclusive -
            manifest.StartInclusive;
        if (duration < TimeSpan.FromHours(48))
        {
            errors.Add(
                $"Measurement window is {duration.TotalHours:0.##} hours; M4 requires at least 48 hours.");
        }

        if (summary.FetchCount == 0)
        {
            errors.Add("Measurement contains no Fetch evidence.");
        }

        if (summary.ParseRunCount == 0)
        {
            errors.Add("Measurement contains no source parse-run evidence.");
        }

        if (summary.Scheduling.WindowDecisionCount == 0)
        {
            errors.Add(
                "Measurement contains no immutable scheduling-decision evidence.");
        }

        if (summary.Scheduling.MissingWindowDecisionCount != 0)
        {
            errors.Add(
                $"{summary.Scheduling.MissingWindowDecisionCount} Fetches in the measurement window have no scheduling decision.");
        }

        if (summary.StorageGrowth is null)
        {
            errors.Add(
                "Storage growth is unavailable; storage-before.json and storage-after.json are required for M4 publication evidence.");
        }

        if (summary.ExecutorCapacity.ModeledEndpointCount == 0)
        {
            errors.Add(
                "Executor capacity could not be modeled from active scheduling decisions.");
        }

        if (manifest.Probe is not null)
        {
            if (summary.Scheduling.ProbeSelectedDecisionCount == 0)
            {
                errors.Add(
                    "Manifest declares a bounded probe but no probe-selected scheduling decisions were measured.");
            }

            if (summary.Scheduling.ProbeAttributedFetchCount == 0)
            {
                errors.Add(
                    "Manifest declares a bounded probe but no Fetches were attributable to probe-created successor jobs.");
            }

            if (summary.Downsampling.Count == 0)
            {
                errors.Add(
                    "Manifest declares a bounded probe but no downsampling series were produced.");
            }
        }
        else if (summary.Scheduling.ProbeSelectedDecisionCount != 0)
        {
            errors.Add(
                "Probe-selected decisions exist but the manifest has no probe configuration.");
        }

        if (summary.ExecutorCapacity.RemainingSerialHeadroom <= 0)
        {
            warnings.Add(
                $"Modeled serial service load is {summary.ExecutorCapacity.RequiredSerialServiceLoad:0.####}; executor-concurrency decision must address insufficient serial headroom.");
        }

        if (summary.Etag.SameEtagDifferentPayloadCount > 0)
        {
            warnings.Add(
                $"{summary.Etag.SameEtagDifferentPayloadCount} same-ETag/different-payload anomalies were observed.");
        }

        if (summary.Etag.UnusableCount > 0)
        {
            warnings.Add(
                $"{summary.Etag.UnusableCount} source ETag values were syntactically unusable as HTTP validators.");
        }

        var versionRegressions = summary.WarApi.Groups.Sum(
            group => group.SourceVersionRegressionCount);
        if (versionRegressions > 0)
        {
            warnings.Add(
                $"{versionRegressions} source version regressions were observed.");
        }

        var lastUpdatedRegressions = summary.WarApi.Groups.Sum(
            group => group.SourceLastUpdatedRegressionCount);
        if (lastUpdatedRegressions > 0)
        {
            warnings.Add(
                $"{lastUpdatedRegressions} source lastUpdated regressions were observed.");
        }

        if (summary.Attempts.UncertainExchangeCount > 0)
        {
            warnings.Add(
                $"{summary.Attempts.UncertainExchangeCount} uncertain exchanges were observed.");
        }

        if (summary.PayloadDecode.MissingDecodedLengthCount > 0)
        {
            warnings.Add(
                $"{summary.PayloadDecode.MissingDecodedLengthCount} parse runs have no decoded byte length; inspect decode failures and pre-M4 evidence.");
        }

        var result = new MeasurementValidationResult(
            errors.Count == 0,
            errors,
            warnings);

        var validationPath = Path.Combine(
            options.OutputDirectory,
            "measurement-validation.json");
        await WriteJsonAsync(
            validationPath,
            result,
            cancellationToken);

        foreach (var warning in warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }

        if (errors.Count == 0)
        {
            Console.WriteLine(
                $"M4 publication evidence is structurally complete. Wrote '{validationPath}'.");
            return 0;
        }

        foreach (var error in errors)
        {
            Console.Error.WriteLine($"ERROR: {error}");
        }

        Console.Error.WriteLine(
            $"M4 publication evidence is incomplete. Wrote '{validationPath}'.");
        return 3;
    }

    private static MeasurementCacheSummary AnalyzeCache(
        IReadOnlyCollection<SourceMeasurementFetch> fetches)
    {
        var policy = new WarApiCachePolicy();
        var cacheControlPresent = 0;
        var cacheControlMalformed = 0;
        var noCache = 0;
        var noStore = 0;
        var expiresPresent = 0;
        var sourceDatePresent = 0;
        var sourceAgePresent = 0;
        var retryAfterPresent = 0;
        var retryAfterMalformed = 0;
        var maxAges = new List<double>();
        var sharedMaxAges = new List<double>();
        var expiresLifetimes = new List<double>();
        var freshnessLifetimes = new List<double>();
        var sourceCacheDelays = new List<double>();
        var sourceAges = new List<double>();
        var retryAfterDelays = new List<double>();

        foreach (var fetch in fetches)
        {
            CacheControlHeaderValue? parsedCacheControl = null;
            if (!string.IsNullOrWhiteSpace(fetch.CacheControl))
            {
                cacheControlPresent++;

                if (!CacheControlHeaderValue.TryParse(
                        fetch.CacheControl,
                        out parsedCacheControl))
                {
                    cacheControlMalformed++;
                }
                else
                {
                    if (parsedCacheControl.NoCache)
                    {
                        noCache++;
                    }

                    if (parsedCacheControl.NoStore)
                    {
                        noStore++;
                    }

                    if (parsedCacheControl.MaxAge is { } maxAge)
                    {
                        maxAges.Add(maxAge.TotalSeconds);
                    }

                    if (parsedCacheControl.SharedMaxAge is { } sharedMaxAge)
                    {
                        sharedMaxAges.Add(sharedMaxAge.TotalSeconds);
                    }
                }
            }

            if (fetch.SourceDate is not null)
            {
                sourceDatePresent++;
            }

            if (fetch.SourceAgeSeconds is not null)
            {
                sourceAgePresent++;
            }

            TimeSpan? expiresLifetime = null;
            if (fetch.ExpiresAt is { } expiresAt)
            {
                expiresPresent++;
                var basis = fetch.SourceDate ?? fetch.RetrievedAt;
                expiresLifetime = expiresAt - basis;
                expiresLifetimes.Add(
                    expiresLifetime.Value.TotalSeconds);
            }

            if (!string.IsNullOrWhiteSpace(fetch.RetryAfter))
            {
                retryAfterPresent++;
                if (!RetryConditionHeaderValue.TryParse(
                        fetch.RetryAfter,
                        out var retryAfter))
                {
                    retryAfterMalformed++;
                }
                else if (retryAfter.Delta is { } delta)
                {
                    retryAfterDelays.Add(
                        Math.Max(0, delta.TotalSeconds));
                }
                else if (retryAfter.Date is { } retryDate)
                {
                    retryAfterDelays.Add(
                        Math.Max(
                            0,
                            (retryDate - fetch.RetrievedAt)
                                .TotalSeconds));
                }
            }

            var explicitLifetime =
                parsedCacheControl?.SharedMaxAge ??
                parsedCacheControl?.MaxAge;

            if (explicitLifetime is null &&
                expiresLifetime is { } expires)
            {
                explicitLifetime =
                    expires > TimeSpan.Zero
                        ? expires
                        : TimeSpan.Zero;
            }

            if (explicitLifetime is { } lifetime)
            {
                freshnessLifetimes.Add(lifetime.TotalSeconds);
            }

            if (fetch.SourceAgeSeconds is { } sourceAge)
            {
                sourceAges.Add(sourceAge);
            }

            var statusCode = fetch.StatusCode is { } status
                ? (HttpStatusCode)status
                : 0;
            var decision = policy.Evaluate(
                new WarApiCacheMetadata(
                    statusCode,
                    fetch.CacheControl,
                    fetch.ExpiresAt,
                    fetch.SourceDate,
                    fetch.SourceAgeSeconds,
                    fetch.RetryAfter),
                fetch.RetrievedAt,
                TimeSpan.Zero);

            sourceCacheDelays.Add(
                Math.Max(
                    0,
                    (decision.SourceCacheEligibleAt - fetch.RetrievedAt)
                        .TotalSeconds));
        }

        return new MeasurementCacheSummary(
            cacheControlPresent,
            cacheControlMalformed,
            noCache,
            noStore,
            expiresPresent,
            sourceDatePresent,
            sourceAgePresent,
            retryAfterPresent,
            retryAfterMalformed,
            Percentiles(maxAges),
            Percentiles(sharedMaxAges),
            Percentiles(expiresLifetimes),
            Percentiles(freshnessLifetimes),
            Percentiles(sourceCacheDelays),
            Percentiles(sourceAges),
            Percentiles(retryAfterDelays));
    }

    private static MeasurementAttemptSummary AnalyzeAttempts(
        IReadOnlyCollection<SourceMeasurementAttempt> attempts)
    {
        var nonNegativeLag = new List<double>();
        var negativeLagCount = 0;

        foreach (var attempt in attempts)
        {
            var lag =
                (attempt.StartedAt - attempt.ScheduledFor)
                .TotalSeconds;

            if (lag < 0)
            {
                negativeLagCount++;
            }
            else
            {
                nonNegativeLag.Add(lag);
            }
        }

        return new MeasurementAttemptSummary(
            attempts.Count,
            attempts.Count(
                attempt =>
                    attempt.ExchangeAuthorizedAt is not null),
            attempts.Count(
                attempt =>
                    attempt.ExchangeAuthorizedAt is null &&
                    string.Equals(
                        attempt.State,
                        "failed",
                        StringComparison.Ordinal)),
            attempts.Count(
                attempt =>
                    string.Equals(
                        attempt.State,
                        "uncertain",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        attempt.OutcomeCode,
                        "uncertain_exchange",
                        StringComparison.Ordinal)),
            attempts.Count(
                attempt =>
                    string.Equals(
                        attempt.State,
                        "captured_late",
                        StringComparison.Ordinal)),
            negativeLagCount,
            Percentiles(nonNegativeLag),
            CountBy(attempts, attempt => attempt.State),
            CountBy(
                attempts,
                attempt =>
                    string.IsNullOrWhiteSpace(attempt.OutcomeCode)
                        ? "<none>"
                        : attempt.OutcomeCode!),
            CountBy(
                attempts,
                attempt =>
                    string.IsNullOrWhiteSpace(attempt.ErrorClass)
                        ? "<none>"
                        : attempt.ErrorClass!));
    }

    private static MeasurementSchedulingSummary AnalyzeScheduling(
        IReadOnlyCollection<SourceMeasurementFetch> fetches,
        IReadOnlyCollection<SourceMeasurementAttempt> attempts,
        IReadOnlyCollection<SourceMeasurementScheduleDecision> decisions)
    {
        var fetchById = fetches.ToDictionary(
            fetch => fetch.FetchId.Value);
        var windowDecisions = decisions
            .Where(
                decision =>
                    fetchById.ContainsKey(
                        decision.FetchId.Value))
            .ToArray();

        var attemptById = attempts.ToDictionary(
            attempt => attempt.AttemptId.Value);
        var decisionBySuccessorJob = decisions
            .Where(decision => decision.SuccessorJobId is not null)
            .ToDictionary(
                decision => decision.SuccessorJobId!.Value.Value);

        var probeAttributed = 0;
        var baselineAttributed = 0;
        var unattributed = 0;

        foreach (var fetch in fetches)
        {
            if (!attemptById.TryGetValue(
                    fetch.AttemptId.Value,
                    out var attempt) ||
                !decisionBySuccessorJob.TryGetValue(
                    attempt.JobId.Value,
                    out var incomingDecision))
            {
                unattributed++;
                continue;
            }

            if (incomingDecision.ProbeSelected)
            {
                probeAttributed++;
            }
            else
            {
                baselineAttributed++;
            }
        }

        var cadenceSeconds = windowDecisions
            .Select(
                decision =>
                    decision.EffectiveCadenceMs / 1000d)
            .ToArray();
        var sourceCacheDelay = new List<double>();
        var retryDelay = new List<double>();
        var successorDelay = new List<double>();
        var successorExtension = new List<double>();

        foreach (var decision in windowDecisions)
        {
            if (!fetchById.TryGetValue(
                    decision.FetchId.Value,
                    out var fetch))
            {
                continue;
            }

            if (decision.SourceCacheEligibleAt is { } cacheEligibleAt)
            {
                sourceCacheDelay.Add(
                    Math.Max(
                        0,
                        (cacheEligibleAt - fetch.RetrievedAt)
                            .TotalSeconds));
            }

            if (decision.RetryEligibleAt is { } retryEligibleAt)
            {
                retryDelay.Add(
                    Math.Max(
                        0,
                        (retryEligibleAt - fetch.RetrievedAt)
                            .TotalSeconds));
            }

            if (decision.SuccessorAvailableAt is { } successorAvailableAt)
            {
                var delay = Math.Max(
                    0,
                    (successorAvailableAt - fetch.RetrievedAt)
                        .TotalSeconds);
                successorDelay.Add(delay);

                var cadenceSecondsForDecision =
                    decision.EffectiveCadenceMs / 1000d;
                successorExtension.Add(
                    Math.Max(
                        0,
                        delay - cadenceSecondsForDecision));
            }
        }

        return new MeasurementSchedulingSummary(
            windowDecisions.Length,
            Math.Max(
                0,
                fetches.Count - windowDecisions.Length),
            windowDecisions.Count(
                decision => decision.ProbeSelected),
            windowDecisions.Count(
                decision => decision.SuccessorJobId is not null),
            probeAttributed,
            baselineAttributed,
            unattributed,
            Percentiles(cadenceSeconds),
            Percentiles(sourceCacheDelay),
            Percentiles(retryDelay),
            Percentiles(successorDelay),
            Percentiles(successorExtension));
    }

    private static IReadOnlyList<MeasurementDownsampleSeries> BuildDownsampling(
        IReadOnlyCollection<SourceMeasurementFetch> fetches,
        IReadOnlyCollection<SourceMeasurementAttempt> attempts,
        IReadOnlyCollection<SourceMeasurementScheduleDecision> decisions,
        IReadOnlyDictionary<Guid, SourceMeasurementParseRun> parseByFetch)
    {
        var attemptById = attempts.ToDictionary(
            attempt => attempt.AttemptId.Value);
        var decisionBySuccessorJob = decisions
            .Where(decision => decision.SuccessorJobId is not null)
            .ToDictionary(
                decision => decision.SuccessorJobId!.Value.Value);

        var probeFetches = fetches
            .Where(fetch =>
            {
                if (!attemptById.TryGetValue(
                        fetch.AttemptId.Value,
                        out var attempt) ||
                    attempt.AttemptNumber != 1 ||
                    !decisionBySuccessorJob.TryGetValue(
                        attempt.JobId.Value,
                        out var incomingDecision))
                {
                    return false;
                }

                return incomingDecision.ProbeSelected;
            })
            .ToArray();

        var candidateCadences = new[]
        {
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
        };

        return probeFetches
            .GroupBy(
                fetch => new
                {
                    fetch.ShardKey,
                    fetch.SemanticKey,
                    fetch.CapabilityKey,
                })
            .Select(group =>
            {
                var capability =
                    CapabilityFromKey(group.Key.CapabilityKey);
                var samples = group
                    .OrderBy(fetch => fetch.RequestStartedAt)
                    .ThenBy(fetch => fetch.FetchId.Value)
                    .Select(fetch =>
                    {
                        _ = parseByFetch.TryGetValue(
                            fetch.FetchId.Value,
                            out var parseRun);

                        return new WarApiMeasurementSample(
                            group.Key.SemanticKey,
                            capability,
                            fetch.RequestStartedAt,
                            fetch.StatusCode,
                            fetch.PayloadSha256Hex,
                            fetch.PayloadBytes,
                            fetch.SourceEtag,
                            fetch.DurationMs,
                            parseRun?.SourceVersion,
                            fetch.StatusCode == 304 &&
                                fetch.PriorFetchId is not null);
                    })
                    .ToArray();

                if (!samples.Any(
                        sample =>
                            sample.StatusCode == 200 &&
                            sample.PayloadHash is not null))
                {
                    return null;
                }

                var candidates = candidateCadences
                    .Select(
                        cadence =>
                            WarApiMeasurementAnalyzer.SimulateCadence(
                                samples,
                                cadence))
                    .ToArray();

                return new MeasurementDownsampleSeries(
                    group.Key.ShardKey,
                    group.Key.SemanticKey,
                    group.Key.CapabilityKey,
                    samples.Length,
                    candidates);
            })
            .Where(
                summary => summary is not null)
            .Select(summary => summary!)
            .OrderBy(
                summary => summary.ShardKey,
                StringComparer.Ordinal)
            .ThenBy(
                summary => summary.CapabilityKey,
                StringComparer.Ordinal)
            .ThenBy(
                summary => summary.EndpointKey,
                StringComparer.Ordinal)
            .ToArray();
    }

    private static MeasurementProbeManifest? BuildProbeManifest(
        AnalyzeOptions options,
        IReadOnlyCollection<SourceMeasurementScheduleDecision> decisions)
    {
        var probeDecisions = decisions
            .Where(decision => decision.ProbeSelected)
            .ToArray();

        var hasConfiguredProbe =
            options.ProbeShardKeys.Count > 0 ||
            options.ProbeMaxMapsPerShard is not null ||
            options.ProbeTargetCadenceSeconds is not null;

        if (!hasConfiguredProbe)
        {
            if (probeDecisions.Length != 0)
            {
                throw new InvalidOperationException(
                    "Probe-selected scheduling decisions exist in the measurement window. Supply --probe-shards, --probe-max-maps and --probe-target-seconds so the manifest records the bounded probe configuration.");
            }

            return null;
        }

        if (options.ProbeShardKeys.Count == 0 ||
            options.ProbeMaxMapsPerShard is null ||
            options.ProbeTargetCadenceSeconds is null)
        {
            throw new ArgumentException(
                "--probe-shards, --probe-max-maps and --probe-target-seconds must be supplied together.");
        }

        var profile = new WarApiMeasurementProbeProfile(
            Enabled: true,
            RunId: options.RunId,
            ShardKeys: options.ProbeShardKeys,
            MaxMapsPerShard: options.ProbeMaxMapsPerShard.Value,
            TargetCadence: TimeSpan.FromSeconds(
                options.ProbeTargetCadenceSeconds.Value));
        profile.Validate();

        var configuredShardSet = new HashSet<string>(
            profile.ShardKeys,
            StringComparer.Ordinal);

        var unexpectedShards = probeDecisions
            .Select(decision => decision.ShardKey)
            .Distinct(StringComparer.Ordinal)
            .Where(shard => !configuredShardSet.Contains(shard))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (unexpectedShards.Length != 0)
        {
            throw new InvalidOperationException(
                $"Observed probe decisions exist outside the configured probe shard set: {string.Join(", ", unexpectedShards)}.");
        }

        var expectedCadenceMs = checked(
            options.ProbeTargetCadenceSeconds.Value * 1000L);
        if (probeDecisions.Any(
                decision =>
                    decision.EffectiveCadenceMs != expectedCadenceMs))
        {
            throw new InvalidOperationException(
                "Observed probe-selected decisions do not match --probe-target-seconds.");
        }

        var policySuffix =
            $"-n{options.ProbeMaxMapsPerShard.Value}-t{options.ProbeTargetCadenceSeconds.Value}s";
        if (probeDecisions.Any(
                decision =>
                    !decision.PolicyVersion.Contains(
                        WarApiMeasurementProbeProfile.Version,
                        StringComparison.Ordinal) ||
                    !decision.PolicyVersion.EndsWith(
                        policySuffix,
                        StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Observed probe policy identities do not match the supplied bounded probe configuration.");
        }

        return new MeasurementProbeManifest(
            WarApiMeasurementProbeProfile.Version,
            options.RunId,
            profile.ShardKeys
                .Order(StringComparer.Ordinal)
                .ToArray(),
            profile.MaxMapsPerShard,
            checked((int)profile.TargetCadence.TotalSeconds));
    }

    private static double? PayloadDeduplicationRatio(
        IReadOnlyCollection<SourceMeasurementFetch> fetches)
    {
        var bodyBearing = fetches
            .Where(fetch => fetch.PayloadSha256Hex is not null)
            .ToArray();

        if (bodyBearing.Length == 0)
        {
            return null;
        }

        var unique = bodyBearing
            .Select(fetch => fetch.PayloadSha256Hex!)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return 1d - ((double)unique / bodyBearing.Length);
    }

    private static async Task<MeasurementStorageGrowth?> TryReadStorageGrowthAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var beforePath =
            Path.Combine(outputDirectory, "storage-before.json");
        var afterPath =
            Path.Combine(outputDirectory, "storage-after.json");

        if (!File.Exists(beforePath) ||
            !File.Exists(afterPath))
        {
            return null;
        }

        var before = JsonSerializer.Deserialize<
            SourceMeasurementStorageSnapshot>(
            await File.ReadAllTextAsync(
                beforePath,
                cancellationToken),
            JsonOptions)
            ?? throw new InvalidOperationException(
                "storage-before.json could not be deserialized.");
        var after = JsonSerializer.Deserialize<
            SourceMeasurementStorageSnapshot>(
            await File.ReadAllTextAsync(
                afterPath,
                cancellationToken),
            JsonOptions)
            ?? throw new InvalidOperationException(
                "storage-after.json could not be deserialized.");

        var elapsed = after.CapturedAt - before.CapturedAt;
        if (elapsed <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Storage snapshots are not in chronological order.");
        }

        var beforeRelations = before.Relations.ToDictionary(
            relation =>
                $"{relation.SchemaName}.{relation.RelationName}",
            StringComparer.Ordinal);
        var afterRelations = after.Relations.ToDictionary(
            relation =>
                $"{relation.SchemaName}.{relation.RelationName}",
            StringComparer.Ordinal);

        if (!beforeRelations.Keys
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    afterRelations.Keys.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Storage snapshots do not contain the same measured relations.");
        }

        var days = elapsed.TotalDays;
        var databaseDelta =
            after.DatabaseBytes - before.DatabaseBytes;
        var databasePerDay = databaseDelta / days;

        var relations = beforeRelations.Keys
            .Order(StringComparer.Ordinal)
            .Select(key =>
            {
                var beforeRelation = beforeRelations[key];
                var afterRelation = afterRelations[key];
                var delta =
                    afterRelation.TotalBytes -
                    beforeRelation.TotalBytes;
                var perDay = delta / days;

                return new MeasurementStorageGrowthRelation(
                    afterRelation.SchemaName,
                    afterRelation.RelationName,
                    delta,
                    perDay,
                    perDay * 30,
                    perDay * 365);
            })
            .ToArray();

        return new MeasurementStorageGrowth(
            before.CapturedAt,
            after.CapturedAt,
            databaseDelta,
            databasePerDay,
            databasePerDay * 30,
            databasePerDay * 365,
            relations);
    }

    private static string RenderMarkdown(
        MeasurementManifest manifest,
        MeasurementSummary summary)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# M4 Source Measurement Report");
        builder.AppendLine();
        builder.AppendLine($"Run: {manifest.RunId}");
        builder.AppendLine(
            $"Window: {manifest.StartInclusive:O} to {manifest.EndExclusive:O}");
        builder.AppendLine(
            $"Repository: {manifest.RepositorySha}");
        builder.AppendLine(
            $"Collection profile: {manifest.CollectionProfileVersion}");
        builder.AppendLine(
            $"Observer region: {manifest.ObserverRegion}");
        builder.AppendLine();
        builder.AppendLine("## Corpus");
        builder.AppendLine();
        builder.AppendLine($"- Fetches: {summary.FetchCount}");
        builder.AppendLine($"- Attempts: {summary.AttemptCount}");
        builder.AppendLine($"- Parse runs: {summary.ParseRunCount}");
        builder.AppendLine($"- Endpoints: {summary.EndpointCount}");
        builder.AppendLine(
            $"- Payload deduplication ratio: {FormatRatio(summary.PayloadDeduplicationRatio)}");
        builder.AppendLine(
            $"- Declared-length mismatches: {summary.DeclaredLengthMismatchCount}");
        builder.AppendLine();

        builder.AppendLine("## Shard / capability");
        builder.AppendLine();
        builder.AppendLine(
            "| Shard | Capability | Endpoints | Fetches | 200 | 304 | Validation hits | Orphan 304 | Other | Duplicate 200 | Changes | Version advances | Version gaps | Version regressions | lastUpdated regressions |");
        builder.AppendLine(
            "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var group in summary.WarApi.Groups)
        {
            builder.AppendLine(
                $"| {group.ShardKey} | {group.CapabilityKey} | {group.EndpointCount} | {group.FetchCount} | {group.OkCount} | {group.NotModifiedCount} | {group.ValidationHitCount} | {group.OrphanNotModifiedCount} | {group.OtherCount} | {group.DuplicateOkCount} | {group.RepresentationChangeCount} | {group.SourceVersionAdvanceCount} | {group.SourceVersionGapCount} | {group.SourceVersionRegressionCount} | {group.SourceLastUpdatedRegressionCount} |");
        }

        builder.AppendLine();
        builder.AppendLine("## ETag");
        builder.AppendLine();
        builder.AppendLine(
            $"- Present: {summary.Etag.PresentCount}");
        builder.AppendLine(
            $"- Strong: {summary.Etag.StrongCount}");
        builder.AppendLine(
            $"- Weak: {summary.Etag.WeakCount}");
        builder.AppendLine(
            $"- Unusable: {summary.Etag.UnusableCount}");
        builder.AppendLine(
            $"- Same ETag / different payload: {summary.Etag.SameEtagDifferentPayloadCount}");
        builder.AppendLine(
            $"- Different ETag / same payload: {summary.Etag.DifferentEtagSamePayloadCount}");
        builder.AppendLine();

        builder.AppendLine("## Payload decoding");
        builder.AppendLine();
        builder.AppendLine(
            $"- Parse runs: {summary.PayloadDecode.ParseRunCount}");
        builder.AppendLine(
            $"- Decoded lengths recorded: {summary.PayloadDecode.DecodedCount}");
        builder.AppendLine(
            $"- Missing decoded lengths: {summary.PayloadDecode.MissingDecodedLengthCount}");
        builder.AppendLine(
            $"- Decoded size p95: {FormatNumber(summary.PayloadDecode.DecodedSizeBytes.P95)} bytes");
        builder.AppendLine(
            $"- Decoded size max: {FormatNumber(summary.PayloadDecode.DecodedSizeBytes.Max)} bytes");
        builder.AppendLine(
            $"- Expansion ratio p95: {FormatNumber(summary.PayloadDecode.ExpansionRatio.P95)}x");
        builder.AppendLine(
            $"- Expansion ratio max: {FormatNumber(summary.PayloadDecode.ExpansionRatio.Max)}x");
        builder.AppendLine();

        builder.AppendLine("## Volume");
        builder.AppendLine();
        builder.AppendLine(
            $"- Window days: {summary.Volume.WindowDays:0.###}");
        builder.AppendLine(
            $"- Fetch rows/day: {summary.Volume.FetchRowsPerDay:0.##}");
        builder.AppendLine(
            $"- Unique Payload rows/day: {summary.Volume.UniquePayloadRowsPerDay:0.##}");
        builder.AppendLine(
            $"- Parse runs/day: {summary.Volume.ParseRunsPerDay:0.##}");
        builder.AppendLine(
            $"- Scheduling decisions/day: {summary.Volume.ScheduleDecisionsPerDay:0.##}");
        builder.AppendLine();

        builder.AppendLine("## Executor capacity");
        builder.AppendLine();
        builder.AppendLine(
            $"- Configured application exchange concurrency: {summary.ExecutorCapacity.ExecutorConcurrency}");
        builder.AppendLine(
            $"- Modeled endpoints: {summary.ExecutorCapacity.ModeledEndpointCount}");
        builder.AppendLine(
            $"- Required serial service load: {summary.ExecutorCapacity.RequiredSerialServiceLoad:0.####}");
        builder.AppendLine(
            $"- Remaining serial headroom: {summary.ExecutorCapacity.RemainingSerialHeadroom:P2}");
        builder.AppendLine(
            $"- Minimum modeled concurrency: {summary.ExecutorCapacity.MinimumModeledConcurrency}");
        builder.AppendLine();

        if (summary.ExecutorCapacity.Groups.Count > 0)
        {
            builder.AppendLine(
                "| Shard | Capability | Endpoints | Required serial load |");
            builder.AppendLine(
                "| --- | --- | ---: | ---: |");

            foreach (var group in summary.ExecutorCapacity.Groups)
            {
                builder.AppendLine(
                    $"| {group.ShardKey} | {group.CapabilityKey} | {group.EndpointCount} | {group.RequiredSerialServiceLoad:0.####} |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Latency by response class");
        builder.AppendLine();
        builder.AppendLine(
            "| Class | p50 ms | p90 ms | p95 ms | p99 ms | max ms |");
        builder.AppendLine(
            "| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var latency in summary.LatencyByResponseClassMs)
        {
            builder.AppendLine(
                $"| {latency.Key} | {FormatNumber(latency.Value.P50)} | {FormatNumber(latency.Value.P90)} | {FormatNumber(latency.Value.P95)} | {FormatNumber(latency.Value.P99)} | {FormatNumber(latency.Value.Max)} |");
        }

        builder.AppendLine();

        builder.AppendLine("## Burst shape");
        builder.AppendLine();
        builder.AppendLine(
            "| Window | Requests | Buckets | Mean | p95 | p99 | Max |");
        builder.AppendLine(
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var burst in summary.WarApi.BurstShape)
        {
            builder.AppendLine(
                $"| {burst.Window.TotalSeconds:0}s | {burst.RequestCount} | {burst.BucketCount} | {burst.MeanRequestsPerBucket:0.###} | {FormatNumber(burst.P95RequestsPerBucket)} | {FormatNumber(burst.P99RequestsPerBucket)} | {burst.MaxRequestsPerBucket} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Attempts");
        builder.AppendLine();
        builder.AppendLine(
            $"- Exchange-authorized: {summary.Attempts.ExchangeAuthorizedCount}");
        builder.AppendLine(
            $"- Pre-exchange failures: {summary.Attempts.PreExchangeFailureCount}");
        builder.AppendLine(
            $"- Uncertain exchange: {summary.Attempts.UncertainExchangeCount}");
        builder.AppendLine(
            $"- Captured late: {summary.Attempts.CapturedLateCount}");
        builder.AppendLine(
            $"- Negative logical-start lag anomalies: {summary.Attempts.NegativeLogicalStartLagCount}");
        builder.AppendLine(
            $"- Logical-start lag p95: {FormatNumber(summary.Attempts.LogicalStartLagSeconds.P95)} s");
        builder.AppendLine();

        builder.AppendLine("## Scheduling");
        builder.AppendLine();
        builder.AppendLine(
            $"- Decisions in window: {summary.Scheduling.WindowDecisionCount}");
        builder.AppendLine(
            $"- Missing decisions: {summary.Scheduling.MissingWindowDecisionCount}");
        builder.AppendLine(
            $"- Probe-selected decisions: {summary.Scheduling.ProbeSelectedDecisionCount}");
        builder.AppendLine(
            $"- Probe-attributed fetches: {summary.Scheduling.ProbeAttributedFetchCount}");
        builder.AppendLine(
            $"- Baseline-attributed fetches: {summary.Scheduling.BaselineAttributedFetchCount}");
        builder.AppendLine(
            $"- Unattributed fetches: {summary.Scheduling.UnattributedFetchCount}");
        builder.AppendLine(
            $"- Effective cadence p95: {FormatNumber(summary.Scheduling.EffectiveCadenceSeconds.P95)} s");
        builder.AppendLine(
            $"- Successor extension beyond cadence p95: {FormatNumber(summary.Scheduling.SuccessorExtensionBeyondCadenceSeconds.P95)} s");
        builder.AppendLine();

        if (summary.Downsampling.Count > 0)
        {
            builder.AppendLine("## Probe downsampling");
            builder.AppendLine();
            builder.AppendLine(
                "| Shard | Endpoint | Candidate | Probe fetches | Episodes | Captured | Ratio | Delay p95 |");
            builder.AppendLine(
                "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");

            foreach (var endpoint in summary.Downsampling)
            {
                foreach (var candidate in endpoint.Candidates)
                {
                    builder.AppendLine(
                        $"| {endpoint.ShardKey} | {endpoint.EndpointKey} | {candidate.CandidateCadence.TotalSeconds:0}s | {endpoint.ProbeAttributedFetchCount} | {candidate.BaselineEpisodeCount} | {candidate.CapturedEpisodeCount} | {candidate.CaptureRatio:P2} | {FormatNumber(candidate.ObservationDelayP95Seconds)} s |");
                }
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Cache");
        builder.AppendLine();
        builder.AppendLine(
            $"- Cache-Control present: {summary.Cache.CacheControlPresentCount}");
        builder.AppendLine(
            $"- Cache-Control malformed: {summary.Cache.CacheControlMalformedCount}");
        builder.AppendLine(
            $"- no-cache: {summary.Cache.NoCacheCount}");
        builder.AppendLine(
            $"- no-store: {summary.Cache.NoStoreCount}");
        builder.AppendLine(
            $"- Retry-After present: {summary.Cache.RetryAfterPresentCount}");
        builder.AppendLine(
            $"- Retry-After malformed: {summary.Cache.RetryAfterMalformedCount}");
        builder.AppendLine(
            $"- Source Date present: {summary.Cache.SourceDatePresentCount}");
        builder.AppendLine(
            $"- Source Age present: {summary.Cache.SourceAgePresentCount}");
        builder.AppendLine(
            $"- max-age p95: {FormatNumber(summary.Cache.MaxAgeSeconds.P95)} s");
        builder.AppendLine(
            $"- s-maxage p95: {FormatNumber(summary.Cache.SharedMaxAgeSeconds.P95)} s");
        builder.AppendLine(
            $"- Expires lifetime p95: {FormatNumber(summary.Cache.ExpiresLifetimeSeconds.P95)} s");
        builder.AppendLine(
            $"- Retry-After delay p95: {FormatNumber(summary.Cache.RetryAfterDelaySeconds.P95)} s");
        builder.AppendLine(
            $"- Source cache delay p95: {FormatNumber(summary.Cache.SourceCacheDelaySeconds.P95)} s");
        builder.AppendLine();

        if (summary.StorageGrowth is { } storage)
        {
            builder.AppendLine("## Storage growth");
            builder.AppendLine();
            builder.AppendLine(
                $"- Database delta: {storage.DatabaseDeltaBytes} bytes");
            builder.AppendLine(
                $"- Database growth/day: {storage.DatabaseBytesPerDay:0.##} bytes");
            builder.AppendLine(
                $"- Projected 30-day growth: {storage.Projected30DayBytes:0.##} bytes");
            builder.AppendLine(
                $"- Projected 365-day growth: {storage.Projected365DayBytes:0.##} bytes");
            builder.AppendLine();
        }

        builder.AppendLine("## Limitations");
        builder.AppendLine();
        foreach (var limitation in manifest.Limitations)
        {
            builder.AppendLine($"- {limitation}");
        }

        return builder.ToString();
    }

    private static MeasurementPercentiles Percentiles(
        IEnumerable<double> values)
    {
        var ordered = values
            .Where(double.IsFinite)
            .Order()
            .ToArray();

        return new MeasurementPercentiles(
            PercentileCont(ordered, 0.50),
            PercentileCont(ordered, 0.90),
            PercentileCont(ordered, 0.95),
            PercentileCont(ordered, 0.99),
            ordered.Length == 0 ? null : ordered[^1]);
    }

    private static double? PercentileCont(
        IReadOnlyList<double> ordered,
        double percentile)
    {
        if (ordered.Count == 0)
        {
            return null;
        }

        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        var position = (ordered.Count - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);

        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var fraction = position - lowerIndex;
        return ordered[lowerIndex] +
            ((ordered[upperIndex] - ordered[lowerIndex]) * fraction);
    }

    private static IReadOnlyDictionary<string, int> CountBy<T>(
        IEnumerable<T> items,
        Func<T, string> selector) =>
        new SortedDictionary<string, int>(
            items
                .GroupBy(selector, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static SourceCapability CapabilityFromKey(string capabilityKey) =>
        capabilityKey switch
        {
            "runtime-war-state" => WarApiCapabilities.RuntimeWarState,
            "active-map-list" => WarApiCapabilities.ActiveMapList,
            "region-war-report" => WarApiCapabilities.RegionWarReport,
            "static-map-state" => WarApiCapabilities.StaticMapState,
            "dynamic-map-state" => WarApiCapabilities.DynamicMapState,
            _ => throw new InvalidOperationException(
                $"Unsupported War API capability '{capabilityKey}' in measurement evidence."),
        };

    private static AnalyzeOptions ParseAnalyzeOptions(string[] args)
    {
        var values = ParseNamedOptions(args);
        RejectUnknown(
            values,
            "run-id",
            "start",
            "end",
            "output",
            "repository-sha",
            "observer-region",
            "profile-version",
            "probe-shards",
            "probe-max-maps",
            "probe-target-seconds");

        var runId = Required(values, "run-id");
        ValidateIdentifier(runId, "run-id");

        var start = ParseTimestamp(
            Required(values, "start"),
            "start");
        var end = ParseTimestamp(
            Required(values, "end"),
            "end");

        if (start >= end)
        {
            throw new ArgumentException(
                "--start must be earlier than --end.");
        }

        var repositorySha = values.TryGetValue(
            "repository-sha",
            out var configuredSha)
            ? configuredSha
            : Environment.GetEnvironmentVariable("GITHUB_SHA") ??
              Environment.GetEnvironmentVariable(
                  "FOXDATA_REPOSITORY_SHA") ??
              throw new ArgumentException(
                  "--repository-sha is required when no GITHUB_SHA or FOXDATA_REPOSITORY_SHA environment variable is set.");

        ValidateIdentifier(repositorySha, "repository-sha");

        var observerRegion = Required(
            values,
            "observer-region");
        ValidateIdentifier(observerRegion, "observer-region");

        var profileVersion = values.TryGetValue(
            "profile-version",
            out var configuredProfile)
            ? configuredProfile
            : WarApiCollectionProfile.Bootstrap.Version;
        ValidateIdentifier(profileVersion, "profile-version");

        var probeShardKeys = values.TryGetValue(
            "probe-shards",
            out var probeShardsValue)
            ? probeShardsValue
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

        int? probeMaxMaps = values.TryGetValue(
            "probe-max-maps",
            out var probeMaxMapsValue)
            ? ParseInt32Option(
                probeMaxMapsValue,
                "probe-max-maps")
            : null;

        int? probeTargetSeconds = values.TryGetValue(
            "probe-target-seconds",
            out var probeTargetSecondsValue)
            ? ParseInt32Option(
                probeTargetSecondsValue,
                "probe-target-seconds")
            : null;

        return new AnalyzeOptions(
            runId,
            start,
            end,
            Path.GetFullPath(Required(values, "output")),
            repositorySha,
            observerRegion,
            profileVersion,
            probeShardKeys,
            probeMaxMaps,
            probeTargetSeconds);
    }

    private static ValidationOptions ParseValidationOptions(
        string[] args)
    {
        var values = ParseNamedOptions(args);
        RejectUnknown(values, "output");

        return new ValidationOptions(
            Path.GetFullPath(Required(values, "output")));
    }

    private static StorageOptions ParseStorageOptions(string[] args)
    {
        var values = ParseNamedOptions(args);
        RejectUnknown(values, "label", "output");

        var label = Required(values, "label");
        if (label is not ("before" or "after"))
        {
            throw new ArgumentException(
                "--label must be 'before' or 'after'.");
        }

        return new StorageOptions(
            label,
            Path.GetFullPath(Required(values, "output")));
    }

    private static Dictionary<string, string> ParseNamedOptions(
        string[] args)
    {
        var values = new Dictionary<string, string>(
            StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) ||
                name.Length <= 2)
            {
                throw new ArgumentException(
                    $"Expected an option name but found '{name}'.");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(
                    $"Option '{name}' requires a value.");
            }

            var key = name[2..];
            if (!values.TryAdd(key, args[index + 1]))
            {
                throw new ArgumentException(
                    $"Option '{name}' was supplied more than once.");
            }
        }

        return values;
    }

    private static void RejectUnknown(
        IReadOnlyDictionary<string, string> values,
        params string[] allowed)
    {
        var allowedSet = new HashSet<string>(
            allowed,
            StringComparer.Ordinal);

        var unknown = values.Keys
            .Where(key => !allowedSet.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (unknown.Length != 0)
        {
            throw new ArgumentException(
                $"Unknown option(s): {string.Join(", ", unknown.Select(key => $"--{key}"))}.");
        }
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"--{key} is required.");
        }

        return value;
    }

    private static int ParseInt32Option(
        string value,
        string option)
    {
        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new ArgumentException(
                $"--{option} must be an integer.");
        }

        return parsed;
    }

    private static DateTimeOffset ParseTimestamp(
        string value,
        string option)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw new ArgumentException(
                $"--{option} must be an ISO-8601 timestamp with an offset.");
        }

        return parsed;
    }

    private static void ValidateIdentifier(
        string value,
        string option)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"--{option} must be non-empty, already trimmed, and at most 256 characters.");
        }
    }

    private static string GetConnectionString()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                "ConnectionStrings__FoxData") ??
            Environment.GetEnvironmentVariable(
                "FOXDATA_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set ConnectionStrings__FoxData or FOXDATA_CONNECTION_STRING before running the measurement tool.");
        }

        return connectionString;
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            value,
            JsonOptions);

        await File.WriteAllTextAsync(
            path,
            json + Environment.NewLine,
            Encoding.UTF8,
            cancellationToken);
    }

    private static string FormatRatio(double? value) =>
        value is null
            ? "n/a"
            : value.Value.ToString("P2", CultureInfo.InvariantCulture);

    private static string FormatNumber(double? value) =>
        value is null
            ? "n/a"
            : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void WriteHelp()
    {
        Console.WriteLine(
            """
            FoxData M4 source measurement tool

            Commands:
              analyze
                --run-id <id>
                --start <ISO-8601>
                --end <ISO-8601>
                --output <directory>
                --observer-region <coarse-region>
                [--repository-sha <sha>]
                [--profile-version <version>]
                [--probe-shards <live-1,live-2>]
                [--probe-max-maps <1..3>]
                [--probe-target-seconds <15..60>]

              storage
                --label <before|after>
                --output <directory>

              validate
                --output <directory>

            Connection string:
              Set ConnectionStrings__FoxData or FOXDATA_CONNECTION_STRING.
              The connection string is never accepted as a command-line argument
              and is never written to measurement artifacts.
            """);
    }
}
