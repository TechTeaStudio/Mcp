using System.Diagnostics.Metrics;
using System.Security.Claims;

namespace TechTeaStudio.Mcp;

/// <summary>How a tool call ended, as the pipeline saw it.</summary>
public enum McpCallOutcome
{
    /// <summary>The tool answered.</summary>
    Ok = 0,

    /// <summary>The call was refused with a message for the model: bad arguments, an <c>McpException</c>, an
    /// exception <see cref="StudioMcpOptions.IsRefusal"/> accepts, or a tool that answered an error itself.</summary>
    Refused = 1,

    /// <summary>The caller was over its limits.</summary>
    RateLimited = 2,

    /// <summary>A read-only tool's answer was over the budget and <see cref="OverBudgetBehavior.Refuse"/> is set.</summary>
    TooLarge = 3,

    /// <summary>An unexpected exception. The model got <see cref="StudioMcpOptions.FailureMessage"/>.</summary>
    Failed = 4,

    /// <summary>The request was cancelled.</summary>
    Cancelled = 5,

    /// <summary>The tool raised a protocol signal (<c>McpProtocolException</c>, e.g. a client capability it needs),
    /// which the SDK answers as a JSON-RPC error.</summary>
    ProtocolError = 6,
}

/// <summary>
/// One finished tool call. Holds no arguments and no answer text, only their shape. A class with init-only
/// properties, so a later version can add one without breaking an observer or a test that builds a record.
/// </summary>
public sealed class McpCallRecord
{
    /// <summary>The tool name.</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>True unless the tool is annotated <c>readOnlyHint: true</c>.</summary>
    public bool IsWrite { get; init; }

    public McpCallOutcome Outcome { get; init; }

    /// <summary>The key the call was counted against (<see cref="StudioMcpOptions.CallerKey"/>).</summary>
    public string Caller { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    /// <summary>The length of the answer's text, 0 when there was none.</summary>
    public int ResultChars { get; init; }

    /// <summary>The authenticated principal on a remote server, null on stdio.</summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>
    /// The exception that ended the call: a refusal the model was told about, the failure behind
    /// <see cref="StudioMcpOptions.FailureMessage"/>, or a protocol signal. Null when the tool answered (even
    /// with an error result of its own), and for a limit, an argument check or the budget. Lets an observer
    /// tell refusals apart, e.g. by a reason the domain exception carries. Server-side only: it is never sent.
    /// </summary>
    public Exception? Error { get; init; }
}

/// <summary>
/// Sees every finished tool call: an activity journal, a product metric, an audit line. Resolved from the
/// request's service scope, so a scoped observer shares the tool's unit of work; that also means it runs
/// after a failed call, where it must not save the tool's half-done changes. An observer that throws is
/// logged and skipped, it never fails the call.
/// </summary>
public interface IMcpCallObserver
{
    ValueTask OnCallCompletedAsync(McpCallRecord call, CancellationToken cancellationToken);
}

/// <summary>
/// The meter the pipeline reports to. Add it to OpenTelemetry with <c>AddMeter(StudioMcpMetrics.MeterName)</c>.
/// Named under <c>techteastudio.</c> on purpose: the SDK already emits the OpenTelemetry MCP metrics under
/// <c>mcp.</c>, and what this adds is the studio outcome of a call (refused, rate limited, too large).
/// </summary>
public static class StudioMcpMetrics
{
    public const string MeterName = "TechTeaStudio.Mcp";

    /// <summary>The tag with the tool's name, as the OpenTelemetry GenAI conventions spell it.</summary>
    public const string ToolTag = "gen_ai.tool.name";

    /// <summary>The tag with the call's <see cref="McpCallOutcome"/>, snake_case.</summary>
    public const string OutcomeTag = "techteastudio.mcp.outcome";

    private static readonly Meter Meter = new(MeterName, typeof(StudioMcpMetrics).Assembly.GetName().Version?.ToString());

    /// <summary><c>techteastudio.mcp.tool.calls</c>: finished calls, tagged by tool and outcome.</summary>
    internal static readonly Counter<long> Calls = Meter.CreateCounter<long>(
        "techteastudio.mcp.tool.calls", unit: "{call}", description: "Finished MCP tool calls by tool and outcome.");

    /// <summary><c>techteastudio.mcp.tool.duration</c>: call duration in seconds, tagged like the counter.</summary>
    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "techteastudio.mcp.tool.duration", unit: "s", description: "Duration of MCP tool calls.");

    internal static void Record(McpCallRecord call)
    {
        var tool = new KeyValuePair<string, object?>(ToolTag, call.Tool);
        var outcome = new KeyValuePair<string, object?>(OutcomeTag, OutcomeName(call.Outcome));
        Calls.Add(1, tool, outcome);
        Duration.Record(call.Duration.TotalSeconds, tool, outcome);
    }

    internal static string OutcomeName(McpCallOutcome outcome) => outcome switch
    {
        McpCallOutcome.Ok => "ok",
        McpCallOutcome.Refused => "refused",
        McpCallOutcome.RateLimited => "rate_limited",
        McpCallOutcome.TooLarge => "too_large",
        McpCallOutcome.Failed => "failed",
        McpCallOutcome.Cancelled => "cancelled",
        McpCallOutcome.ProtocolError => "protocol_error",
        _ => "unknown",
    };
}
