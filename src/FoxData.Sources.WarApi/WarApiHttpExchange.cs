using System.Diagnostics;
using System.Net;

namespace FoxData.Sources.WarApi;

public sealed class WarApiResponseLimitException : Exception
{
    public WarApiResponseLimitException(string message)
        : base(message)
    {
    }
}

public sealed record WarApiHttpExchangeResult(
    HttpStatusCode StatusCode,
    byte[]? Body,
    DateTimeOffset RequestStartedAt,
    DateTimeOffset ResponseStartedAt,
    DateTimeOffset RetrievedAt,
    long DurationMs,
    string? MediaType,
    string? ContentEncoding,
    long? DeclaredLength,
    string? Etag,
    string? CacheControl,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? SourceDate,
    TimeSpan? Age,
    string? RetryAfter,
    string? BodyErrorCode = null);

public sealed class WarApiHttpExchange
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _exchangeTimeout;
    private readonly int _maximumBodyBytes;

    public WarApiHttpExchange(
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        int maximumBodyBytes)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        if (exchangeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exchangeTimeout),
                exchangeTimeout,
                "Exchange timeout must be positive.");
        }

        if (maximumBodyBytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBodyBytes),
                maximumBodyBytes,
                "Maximum body bytes must be positive.");
        }

        _exchangeTimeout = exchangeTimeout;
        _maximumBodyBytes = maximumBodyBytes;
    }

    public async Task<WarApiHttpExchangeResult> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        var requestStartedAt = _timeProvider.GetUtcNow();
        var startedTimestamp = _timeProvider.GetTimestamp();

        using var deadline = new CancellationTokenSource(
            _exchangeTimeout,
            _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            linked.Token);

        var responseStartedAt = _timeProvider.GetUtcNow();
        byte[]? body = null;
        string? bodyErrorCode = null;

        if (response.StatusCode is not HttpStatusCode.NotModified)
        {
            try
            {
                body = await ReadBoundedBodyAsync(
                    response.Content,
                    _maximumBodyBytes,
                    linked.Token);
            }
            catch (WarApiResponseLimitException)
            {
                bodyErrorCode = "body_limit_exceeded";
            }
            catch (IOException)
            {
                bodyErrorCode = "body_read_failed";
            }
            catch (HttpRequestException)
            {
                bodyErrorCode = "body_read_failed";
            }
            catch (OperationCanceledException)
            {
                bodyErrorCode = cancellationToken.IsCancellationRequested
                    ? "body_cancelled"
                    : "body_timeout";
            }
        }

        var retrievedAt = _timeProvider.GetUtcNow();
        var duration = _timeProvider.GetElapsedTime(startedTimestamp);

        return new WarApiHttpExchangeResult(
            response.StatusCode,
            body,
            requestStartedAt,
            responseStartedAt,
            retrievedAt,
            checked((long)duration.TotalMilliseconds),
            response.Content.Headers.ContentType?.ToString(),
            JoinHeaderValues(response.Content.Headers.ContentEncoding),
            response.Content.Headers.ContentLength,
            response.Headers.ETag?.ToString(),
            response.Headers.CacheControl?.ToString(),
            response.Content.Headers.Expires,
            response.Headers.Date,
            response.Headers.Age,
            response.Headers.RetryAfter?.ToString(),
            bodyErrorCode);
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContent content,
        int maximumBodyBytes,
        CancellationToken cancellationToken)
    {
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength > maximumBodyBytes)
        {
            throw new WarApiResponseLimitException(
                $"Declared response body length {declaredLength} exceeds the limit of {maximumBodyBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(
            declaredLength is > 0 and <= int.MaxValue
                ? checked((int)declaredLength.Value)
                : 0);

        var chunk = new byte[Math.Min(81920, maximumBodyBytes)];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBodyBytes)
            {
                throw new WarApiResponseLimitException(
                    $"Response body exceeds the limit of {maximumBodyBytes} bytes.");
            }

            await buffer.WriteAsync(
                chunk.AsMemory(0, read),
                cancellationToken);
        }
    }

    private static string? JoinHeaderValues(ICollection<string> values) =>
        values.Count == 0
            ? null
            : string.Join(", ", values);
}
