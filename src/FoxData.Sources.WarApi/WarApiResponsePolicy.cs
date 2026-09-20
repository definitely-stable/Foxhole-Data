using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public enum WarApiResponseClass
{
    SuccessfulRepresentation,
    NotModified,
    RedirectFailure,
    ClientFailure,
    AuthorizationFailure,
    RootContractFailure,
    MapUnavailable,
    RetryableFailure,
    ServerFailure,
    UnexpectedFailure,
}

public static class WarApiResponsePolicy
{
    public const string BackoffPolicyVersion = "warapi-backoff@1";
    public const string SchedulePolicyVersion = "warapi-schedule@1";

    public static WarApiResponseClass Classify(
        HttpStatusCode statusCode,
        bool mapScoped)
    {
        var code = (int)statusCode;

        if (statusCode == HttpStatusCode.OK)
        {
            return WarApiResponseClass.SuccessfulRepresentation;
        }

        if (statusCode == HttpStatusCode.NotModified)
        {
            return WarApiResponseClass.NotModified;
        }

        if (code is >= 300 and < 400)
        {
            return WarApiResponseClass.RedirectFailure;
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return WarApiResponseClass.AuthorizationFailure;
        }

        if (statusCode == HttpStatusCode.NotFound)
        {
            return mapScoped
                ? WarApiResponseClass.MapUnavailable
                : WarApiResponseClass.RootContractFailure;
        }

        if (statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooEarly or
            HttpStatusCode.TooManyRequests)
        {
            return WarApiResponseClass.RetryableFailure;
        }

        if (code is >= 500 and <= 599)
        {
            return WarApiResponseClass.ServerFailure;
        }

        if (code is >= 400 and <= 499)
        {
            return WarApiResponseClass.ClientFailure;
        }

        return WarApiResponseClass.UnexpectedFailure;
    }

    public static TimeSpan Backoff(
        int consecutiveFailures,
        string stableEndpointKey)
    {
        if (consecutiveFailures < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailures),
                consecutiveFailures,
                "Failure count must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stableEndpointKey);

        var baseSeconds = consecutiveFailures switch
        {
            1 => 15,
            2 => 30,
            3 => 60,
            4 => 120,
            _ => 300,
        };

        var jitterBasis = StableUInt32(
            $"{BackoffPolicyVersion}\n{stableEndpointKey}\n{consecutiveFailures}");
        var jitter = 0.80 + ((jitterBasis % 4001) / 10000.0);

        return TimeSpan.FromSeconds(Math.Min(300, baseSeconds * jitter));
    }

    public static TimeSpan Cadence(SourceCapability capability)
    {
        if (capability == WarApiCapabilities.RuntimeWarState ||
            capability == WarApiCapabilities.RegionWarReport ||
            capability == WarApiCapabilities.DynamicMapState)
        {
            return TimeSpan.FromMinutes(1);
        }

        if (capability == WarApiCapabilities.ActiveMapList)
        {
            return TimeSpan.FromMinutes(5);
        }

        if (capability == WarApiCapabilities.StaticMapState)
        {
            return TimeSpan.FromHours(6);
        }

        throw new ArgumentException(
            $"Unsupported War API capability '{capability.Key}'.",
            nameof(capability));
    }

    public static TimeSpan Spread(
        string shardKey,
        string semanticKey,
        TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window),
                window,
                "Spread window must be positive.");
        }

        var hash = StableUInt64(
            $"{SchedulePolicyVersion}\n{shardKey}\n{semanticKey}");
        var ticks = (long)(hash % (ulong)window.Ticks);

        return TimeSpan.FromTicks(ticks);
    }

    private static uint StableUInt32(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }

    private static ulong StableUInt64(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }
}
