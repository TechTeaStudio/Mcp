using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TechTeaStudio.Mcp;

/// <summary>
/// The request filter <c>AddStudioMcp</c> installs around every tool. Filters run around the tool and see its
/// exceptions before the SDK turns them into "An error occurred invoking 'tool'.", which is what lets a refusal
/// reach the model as the sentence it was written as, and an unexpected failure reach it as nothing secret.
/// </summary>
internal static class StudioMcpPipeline
{
    internal const string UnreadableArguments =
        "The arguments could not be read. Check each value against the tool's input schema: its type and, for a list of allowed values, the exact spelling.";

    private static int _warnedAboutAnonymous;

    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool(StudioMcpOptions options)
        => next => async (context, cancellationToken) =>
        {
            // An unknown tool: the SDK answers the protocol error itself, which is right, and there is no
            // annotation to count it by.
            if (context.MatchedPrimitive is not McpServerTool tool)
                return await next(context, cancellationToken).ConfigureAwait(false);

            var started = Stopwatch.GetTimestamp();
            var name = tool.ProtocolTool.Name;
            var isWrite = tool.ProtocolTool.Annotations?.ReadOnlyHint != true;
            var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("TechTeaStudio.Mcp");
            var caller = CallerOf(options, context, logger);

            Ended ended;
            try
            {
                ended = await RunAsync(next, context, cancellationToken, tool, options, isWrite, caller, logger).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await CompleteAsync(context, name, isWrite, caller, new Ended(null, McpCallOutcome.Cancelled, ex), started, logger, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (McpProtocolException ex)
            {
                // A protocol signal (a missing client capability, an elicitation the client must open) is not a
                // refusal; the SDK answers it as the JSON-RPC error it is. It is still a call that ended.
                await CompleteAsync(context, name, isWrite, caller, new Ended(null, McpCallOutcome.ProtocolError, ex), started, logger, cancellationToken).ConfigureAwait(false);
                throw;
            }

            await CompleteAsync(context, name, isWrite, caller, ended, started, logger, cancellationToken).ConfigureAwait(false);
            return ended.Result!;
        };

    /// <summary>How a call ended: the answer sent (null when cancelled), the outcome and the exception behind it.</summary>
    private readonly record struct Ended(CallToolResult? Result, McpCallOutcome Outcome, Exception? Error = null);

    private static async ValueTask<Ended> RunAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken,
        McpServerTool tool,
        StudioMcpOptions options,
        bool isWrite,
        string caller,
        ILogger? logger)
    {
        var name = tool.ProtocolTool.Name;
        if (options.Limits is { } limits && context.Services?.GetService<IMcpCallLimiter>() is { } limiter)
        {
            var request = new McpLimitRequest { Caller = caller, Tool = name, IsWrite = isWrite, Limits = limits };
            var decision = await limiter.TryAcquireAsync(request, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
                return new(Error($"Too many requests from this connection. Try again in {Wait(decision.RetryAfter)}."), McpCallOutcome.RateLimited);
        }

        if (options.ValidateArguments)
        {
            var arguments = context.Params?.Arguments;
            McpArgumentGuard.DropNullsForDefaults(tool.ProtocolTool.InputSchema, arguments);
            if (McpArgumentGuard.Check(tool.ProtocolTool.InputSchema, arguments, options.RejectUnknownArguments) is { } problem)
                return new(Error(problem), McpCallOutcome.Refused);
        }

        CallToolResult answer;
        try
        {
            answer = await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (McpException ex)
        {
            return new(Error(ex.Message), McpCallOutcome.Refused, ex);
        }
        catch (Exception ex) when (options.IsRefusal?.Invoke(ex) == true)
        {
            return new(Error(ex.Message), McpCallOutcome.Refused, ex);
        }
        catch (JsonException ex)
        {
            // Most often the SDK's binder on an argument the schema check could not judge; it can also be the
            // tool's own JSON code, which is why it is logged and not just answered.
            logger?.LogWarning(ex, "MCP tool {Tool} could not read its arguments or its own JSON", name);
            return new(Error(UnreadableArguments), McpCallOutcome.Refused, ex);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "MCP tool {Tool} failed", name);
            return new(Error(options.FailureMessage), McpCallOutcome.Failed, ex);
        }

        if (answer.IsError == true)
            return new(answer, McpCallOutcome.Refused);

        var chars = TextLength(answer);
        if (chars > options.MaxResultChars)
        {
            // A writing tool's change has already happened: refusing its answer would make the model retry the
            // write. Only a read is refused; a write is logged.
            if (options.OverBudget == OverBudgetBehavior.Refuse && !isWrite)
                return new(Error(string.Format(CultureInfo.InvariantCulture, options.OverBudgetMessage, chars, options.MaxResultChars)), McpCallOutcome.TooLarge);

            logger?.LogWarning("MCP tool {Tool} answered {Chars} characters, over the budget of {Budget}", name, chars, options.MaxResultChars);
        }

        return new(answer, McpCallOutcome.Ok);
    }

    private static async ValueTask CompleteAsync(
        RequestContext<CallToolRequestParams> context,
        string tool,
        bool isWrite,
        string caller,
        Ended ended,
        long started,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var elapsed = TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - started) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
        var call = new McpCallRecord
        {
            Tool = tool,
            IsWrite = isWrite,
            Outcome = ended.Outcome,
            Caller = caller,
            Duration = elapsed,
            ResultChars = ended.Result is null ? 0 : TextLength(ended.Result),
            User = context.User,
            Error = ended.Error,
        };
        StudioMcpMetrics.Record(call);

        if (context.Services is not { } services) return;
        foreach (var observer in services.GetServices<IMcpCallObserver>())
        {
            try
            {
                await observer.OnCallCompletedAsync(call, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // An observer's own failure, its own timeout included, never fails a call that has ended.
                logger?.LogWarning(ex, "MCP call observer {Observer} failed for {Tool}", observer.GetType().Name, tool);
            }
        }
    }

    private static string CallerOf(StudioMcpOptions options, RequestContext<CallToolRequestParams> context, ILogger? logger)
    {
        var custom = options.CallerKey?.Invoke(context);
        if (!string.IsNullOrEmpty(custom)) return custom!;

        var user = context.User;
        var id = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;
        if (id is not null) return id;

        if (user?.Identity?.IsAuthenticated == true && Interlocked.Exchange(ref _warnedAboutAnonymous, 1) == 0)
            logger?.LogWarning("An authenticated MCP caller has no NameIdentifier or sub claim, so every such caller shares one rate-limit budget. Set StudioMcpOptions.CallerKey.");
        return "local";
    }

    /// <summary>A wait the model can read: seconds under two minutes, then minutes, then hours.</summary>
    internal static string Wait(TimeSpan wait)
    {
        if (wait < TimeSpan.FromMinutes(2))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            return seconds.ToString(CultureInfo.InvariantCulture) + (seconds == 1 ? " second" : " seconds");
        }

        if (wait < TimeSpan.FromHours(2))
            return ((int)Math.Ceiling(wait.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " minutes";

        return ((int)Math.Ceiling(wait.TotalHours)).ToString(CultureInfo.InvariantCulture) + " hours";
    }

    internal static CallToolResult Error(string message)
        => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    internal static int TextLength(CallToolResult result)
    {
        var length = 0;
        foreach (var block in result.Content)
            if (block is TextContentBlock text) length += text.Text?.Length ?? 0;

        if (length == 0 && result.StructuredContent is { } structured)
            length = structured.GetRawText().Length;
        return length;
    }
}
