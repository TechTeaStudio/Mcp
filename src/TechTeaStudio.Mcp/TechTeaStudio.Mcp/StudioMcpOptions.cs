using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TechTeaStudio.Mcp;

/// <summary>
/// What <see cref="StudioMcpServiceCollectionExtensions.AddStudioMcp"/> needs to know about a server, and how
/// strict its call pipeline is. <see cref="Name"/>, <see cref="Title"/> and <see cref="Instructions"/> are
/// required: a server that does not say who it is leaves the model guessing which of its connected servers
/// a request is meant for.
/// </summary>
public sealed class StudioMcpOptions
{
    /// <summary>The programmatic name clients log and show, e.g. <c>chronos</c>. Required.</summary>
    public string? Name { get; set; }

    /// <summary>The display name, e.g. <c>Chronos</c>. Required.</summary>
    public string? Title { get; set; }

    /// <summary>The server version. Defaults to the entry assembly's informational version, without the commit hash.</summary>
    public string? Version { get; set; }

    /// <summary>The product's home page, sent with the server identity. Optional.</summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>
    /// The instructions clients hand to the model as system context: what the product is, the words its tools
    /// use, and where to start. Required. Name the product here, because a client may show the model nothing
    /// else about which server is which.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>The JSON the studio helpers register tools and prompts with. <see cref="StudioMcpJson.SnakeCase"/> by default.</summary>
    public JsonSerializerOptions Json { get; set; } = StudioMcpJson.SnakeCase;

    /// <summary>Per-caller limits. Null (the default) turns limiting off, which suits a local stdio server.</summary>
    public McpCallLimits? Limits { get; set; }

    /// <summary>
    /// Who a call is counted against. Defaults to the caller's <c>NameIdentifier</c> or <c>sub</c> claim, else
    /// <c>local</c>. Remote servers usually key on the connection (one token or one app grant) instead, so one
    /// person's two apps do not share a budget.
    /// </summary>
    public Func<RequestContext<CallToolRequestParams>, string?>? CallerKey { get; set; }

    /// <summary>
    /// Which exceptions are refusals whose message the model should read, besides <c>McpException</c>, e.g.
    /// <c>ex =&gt; ex is DomainException</c>. Their message goes out as is, so it must be written for the person
    /// and the model: what happened and what to change.
    /// </summary>
    public Func<Exception, bool>? IsRefusal { get; set; }

    /// <summary>What a call that failed unexpectedly answers. The exception itself is logged, never sent.</summary>
    public string FailureMessage { get; set; } = "Something went wrong on our side. Try again in a moment.";

    /// <summary>Check arguments against the tool's input schema before the tool runs. On by default.</summary>
    public bool ValidateArguments { get; set; } = true;

    /// <summary>
    /// With <see cref="ValidateArguments"/>, refuse an argument the tool does not have, naming the ones it has.
    /// On by default: the SDK's binder drops an unknown name without a word, so a misspelt optional argument
    /// (<c>in_progress</c> for <c>in_work</c>) would silently do nothing.
    /// </summary>
    public bool RejectUnknownArguments { get; set; } = true;

    /// <summary>
    /// The largest answer, in characters, a call should send. 40 000 is about 10 000 tokens, where Claude Code
    /// starts warning; it cuts answers at 25 000 tokens. What happens above it is <see cref="OverBudget"/>.
    /// A writing tool's answer is never refused, only logged: its change has already happened, and a refusal
    /// would make the model retry it.
    /// </summary>
    public int MaxResultChars { get; set; } = 40_000;

    /// <summary>Log a warning (the default) or refuse an answer over <see cref="MaxResultChars"/>.</summary>
    public OverBudgetBehavior OverBudget { get; set; } = OverBudgetBehavior.Warn;

    /// <summary>The refusal for an answer over budget. <c>{0}</c> is its length, <c>{1}</c> the budget.</summary>
    public string OverBudgetMessage { get; set; } =
        "The answer is {0} characters, more than the {1} this server sends at once. Ask for less: a shorter period, fewer rows or a narrower filter.";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("StudioMcpOptions.Name is required: the programmatic server name, e.g. \"chronos\".", nameof(Name));
        if (string.IsNullOrWhiteSpace(Title))
            throw new ArgumentException("StudioMcpOptions.Title is required: the display name, e.g. \"Chronos\".", nameof(Title));
        if (string.IsNullOrWhiteSpace(Instructions))
            throw new ArgumentException("StudioMcpOptions.Instructions are required: what the product is and how to use its tools.", nameof(Instructions));
        if (Json is null)
            throw new ArgumentException("StudioMcpOptions.Json cannot be null.", nameof(Json));
        if (MaxResultChars < 1)
            throw new ArgumentException("StudioMcpOptions.MaxResultChars must be positive.", nameof(MaxResultChars));
        if (string.IsNullOrWhiteSpace(FailureMessage))
            throw new ArgumentException("StudioMcpOptions.FailureMessage cannot be empty.", nameof(FailureMessage));
        try
        {
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, OverBudgetMessage ?? string.Empty, 1, 1);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("StudioMcpOptions.OverBudgetMessage is not a valid format string: {0} is the answer's length, {1} the budget. Double a literal brace.", nameof(OverBudgetMessage), ex);
        }
        if (string.IsNullOrWhiteSpace(OverBudgetMessage))
            throw new ArgumentException("StudioMcpOptions.OverBudgetMessage cannot be empty.", nameof(OverBudgetMessage));
        if (Limits is { } limits)
            limits.Validate();
    }
}

/// <summary>What happens to an answer longer than <see cref="StudioMcpOptions.MaxResultChars"/>.</summary>
public enum OverBudgetBehavior
{
    /// <summary>Send it and log a warning.</summary>
    Warn = 0,

    /// <summary>Answer an error that tells the model to ask for less.</summary>
    Refuse = 1,
}

/// <summary>
/// Per-caller limits, counted per tool call. A tool whose <c>readOnlyHint</c> is not true counts as a write,
/// so a tool that forgot its annotations is held to the stricter limit.
/// </summary>
public sealed class McpCallLimits
{
    public int CallsPerMinute { get; set; } = 30;

    public int WritesPerMinute { get; set; } = 10;

    public int CallsPerDay { get; set; } = 1000;

    internal void Validate()
    {
        if (CallsPerMinute < 1 || WritesPerMinute < 1 || CallsPerDay < 1)
            throw new ArgumentException("McpCallLimits values must be positive.");
    }
}
