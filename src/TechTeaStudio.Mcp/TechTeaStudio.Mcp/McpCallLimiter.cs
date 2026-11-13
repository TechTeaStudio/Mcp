using System.Collections.Concurrent;

namespace TechTeaStudio.Mcp;

/// <summary>Whether a call may run now, and if not, how long until it would.</summary>
public readonly struct McpLimitDecision
{
    public McpLimitDecision(bool allowed, TimeSpan retryAfter)
    {
        Allowed = allowed;
        RetryAfter = retryAfter;
    }

    public bool Allowed { get; }

    public TimeSpan RetryAfter { get; }

    public static McpLimitDecision Pass { get; } = new(true, TimeSpan.Zero);
}

/// <summary>What one call asks the limiter. A class, so a later version can add context without breaking a limiter.</summary>
public sealed class McpLimitRequest
{
    /// <summary>The key the call is counted against (<see cref="StudioMcpOptions.CallerKey"/>).</summary>
    public string Caller { get; init; } = string.Empty;

    /// <summary>The tool being called, for a limiter that budgets some tools apart.</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>True unless the tool is annotated <c>readOnlyHint: true</c>.</summary>
    public bool IsWrite { get; init; }

    public McpCallLimits Limits { get; init; } = new();
}

/// <summary>
/// Counts tool calls against <see cref="McpCallLimits"/>. Register your own before <c>AddStudioMcp</c> to share
/// counts between processes, e.g. through Redis; the call is asynchronous for exactly that.
/// </summary>
public interface IMcpCallLimiter
{
    /// <summary>Counts the call when it passes; a refused call is not counted.</summary>
    ValueTask<McpLimitDecision> TryAcquireAsync(McpLimitRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Fixed one-minute and one-day windows per caller, in memory. A restart forgets the counts, which only ever
/// errs towards letting a call through; run one instance per process that serves the endpoint.
/// </summary>
public sealed class InMemoryMcpCallLimiter : IMcpCallLimiter
{
    private const int SweepAbove = 5000;

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryMcpCallLimiter(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    public ValueTask<McpLimitDecision> TryAcquireAsync(McpLimitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<McpLimitDecision>(TryAcquire(request.Caller, request.IsWrite, request.Limits));
    }

    private McpLimitDecision TryAcquire(string caller, bool isWrite, McpCallLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var now = _time.GetUtcNow();
        var bucket = _buckets.GetOrAdd(caller ?? string.Empty, _ => new Bucket(now));
        lock (bucket)
        {
            if (now - bucket.DayStart >= Day) { bucket.DayStart = now; bucket.DayCalls = 0; }
            if (now - bucket.MinuteStart >= Minute) { bucket.MinuteStart = now; bucket.Calls = 0; bucket.Writes = 0; }

            if (bucket.DayCalls >= limits.CallsPerDay) return Refuse(bucket.DayStart + Day - now);
            if (bucket.Calls >= limits.CallsPerMinute) return Refuse(bucket.MinuteStart + Minute - now);
            if (isWrite && bucket.Writes >= limits.WritesPerMinute) return Refuse(bucket.MinuteStart + Minute - now);

            bucket.Calls++;
            bucket.DayCalls++;
            if (isWrite) bucket.Writes++;
        }

        if (_buckets.Count > SweepAbove) Sweep(now);
        return McpLimitDecision.Pass;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var pair in _buckets)
            if (now - pair.Value.DayStart >= Day) _buckets.TryRemove(pair.Key, out _);
    }

    private static McpLimitDecision Refuse(TimeSpan wait)
        => new(false, wait <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Ceiling(wait.TotalSeconds)));

    private sealed class Bucket
    {
        public Bucket(DateTimeOffset now)
        {
            MinuteStart = now;
            DayStart = now;
        }

        public DateTimeOffset MinuteStart;
        public int Calls;
        public int Writes;
        public DateTimeOffset DayStart;
        public int DayCalls;
    }
}
