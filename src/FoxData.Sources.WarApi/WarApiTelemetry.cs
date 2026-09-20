using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FoxData.Sources.WarApi;

public static class WarApiTelemetry
{
    public const string InstrumentationName = "FoxData.WarApi";

    public static readonly ActivitySource ActivitySource =
        new(InstrumentationName);

    public static readonly Meter Meter =
        new(InstrumentationName);

    public static readonly Counter<long> Requests =
        Meter.CreateCounter<long>(
            "foxdata.source.requests",
            description: "Application-issued source requests.");

    public static readonly Counter<long> Responses =
        Meter.CreateCounter<long>(
            "foxdata.source.responses",
            description: "Source HTTP responses by bounded outcome class.");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "foxdata.source.request.duration",
            unit: "ms",
            description: "Source request duration.");

    public static readonly Counter<long> ResponseBytes =
        Meter.CreateCounter<long>(
            "foxdata.source.response.bytes",
            unit: "By",
            description: "Captured source response content bytes.");

    public static readonly Counter<long> UncertainExchanges =
        Meter.CreateCounter<long>(
            "foxdata.source.exchange.uncertain",
            description: "Authorized source exchanges with uncertain durable outcome.");

    public static readonly Counter<long> ParseRuns =
        Meter.CreateCounter<long>(
            "foxdata.source.parse.runs",
            description: "Versioned source parse outcomes.");

    public static readonly Counter<long> Reconciliations =
        Meter.CreateCounter<long>(
            "foxdata.source.reconciliations",
            description: "Durable source reconciliation outcomes.");
}
