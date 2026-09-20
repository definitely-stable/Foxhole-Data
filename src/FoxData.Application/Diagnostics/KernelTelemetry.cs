using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FoxData.Application.Diagnostics;

internal static class KernelTelemetry
{
    public const string InstrumentationName = "FoxData.EvidenceKernel";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    public static readonly Meter Meter = new(InstrumentationName);

    public static readonly Counter<long> FetchesCaptured =
        Meter.CreateCounter<long>("foxdata.evidence.fetches.captured");

    public static readonly Counter<long> PayloadBytes =
        Meter.CreateCounter<long>("foxdata.evidence.payload.bytes", unit: "By");

    public static readonly Counter<long> PayloadDedupeHits =
        Meter.CreateCounter<long>("foxdata.evidence.payload.dedupe_hits");

    public static readonly Histogram<double> CaptureDuration =
        Meter.CreateHistogram<double>("foxdata.evidence.capture.duration", unit: "ms");

    public static readonly Counter<long> RecoveryItems =
        Meter.CreateCounter<long>("foxdata.ingest.recovery.items");

    public static readonly Counter<long> AttemptsUncertain =
        Meter.CreateCounter<long>("foxdata.ingest.attempts.uncertain");
}
