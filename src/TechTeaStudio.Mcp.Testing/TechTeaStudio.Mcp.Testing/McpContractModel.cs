using System.Text;

namespace TechTeaStudio.Mcp.Testing;

/// <summary>How bad a contract finding is. A test fails on errors; warnings are reported alongside.</summary>
public enum McpContractSeverity
{
    Warning = 0,
    Error = 1,
}

/// <summary>One finding: which rule, how bad, where (the server, a tool, <c>tool.parameter</c>) and why.</summary>
public sealed record McpContractViolation(string RuleId, McpContractSeverity Severity, string Target, string Message)
{
    public override string ToString() => $"{RuleId} {(Severity == McpContractSeverity.Error ? "error" : "warning")} [{Target}] {Message}";
}

/// <summary>A tool call the contract makes, whether the answer should be an error, and a fragment that error must contain.</summary>
public sealed record McpProbe(string Tool, IReadOnlyDictionary<string, object?> Arguments, bool ExpectError, string? ExpectedText = null);

/// <summary>
/// What the contract checks against. Every threshold has a studio default; <see cref="Probe"/> and
/// <see cref="ProbeError"/> add tool calls whose answers are checked too, and <see cref="Allow"/> waives a
/// rule where breaking it is deliberate.
/// </summary>
public sealed class McpContractOptions
{
    private readonly List<McpProbe> _probes = new();
    private readonly List<(string RuleId, string? Target)> _allowed = new();
    private readonly Dictionary<string, McpContractSeverity> _severities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The product's name as people say it, e.g. <c>Chronos</c>. When set, the instructions must name it.</summary>
    public string? ProductName { get; set; }

    public int MinInstructionsLength { get; set; } = 80;

    public int MaxInstructionsLength { get; set; } = 4_000;

    public int MinDescriptionLength { get; set; } = 20;

    /// <summary>Some clients cut tool descriptions at 1 024 characters.</summary>
    public int MaxDescriptionLength { get; set; } = 1_024;

    /// <summary>The largest answer a probe may get, in characters (about 10 000 tokens by default).</summary>
    public int MaxResultChars { get; set; } = 40_000;

    /// <summary>The largest <c>tools/list</c>, in characters of JSON: every tool definition rides along in the model's context.</summary>
    public int MaxToolListChars { get; set; } = 40_000;

    /// <summary>
    /// Error texts that mean the tool crashed, not that it refused: a probe expecting a refusal that gets one of
    /// these fails. Holds the studio pipeline's default; add the server's own <c>FailureMessage</c> if it set one.
    /// </summary>
    public IList<string> FailureMessages { get; } = new List<string> { "Something went wrong on our side. Try again in a moment." };

    public IReadOnlyList<McpProbe> Probes => _probes;

    /// <summary>Calls <paramref name="tool"/> and expects an answer.</summary>
    public McpContractOptions Probe(string tool, object? arguments = null) => Add(tool, arguments, expectError: false);

    /// <summary>
    /// Calls <paramref name="tool"/> with arguments it must refuse, and expects an error a model can act on: not a
    /// crash (<see cref="FailureMessages"/>) and, when given, one whose text contains <paramref name="containing"/>.
    /// </summary>
    public McpContractOptions ProbeError(string tool, object? arguments = null, string? containing = null)
        => Add(tool, arguments, expectError: true, containing);

    /// <summary>Reports <paramref name="ruleId"/> at <paramref name="severity"/> instead of its default, e.g. to make a studio rule a warning.</summary>
    public McpContractOptions Severity(string ruleId, McpContractSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentException("A rule id is required, e.g. TTS-MCP-016.", nameof(ruleId));
        _severities[ruleId.Trim()] = severity;
        return this;
    }

    /// <summary>Waives <paramref name="ruleId"/> for <paramref name="target"/>, or everywhere when it is null.</summary>
    public McpContractOptions Allow(string ruleId, string? target = null)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentException("A rule id is required, e.g. TTS-MCP-014.", nameof(ruleId));
        _allowed.Add((ruleId.Trim(), target));
        return this;
    }

    internal McpContractViolation Apply(McpContractViolation violation)
        => _severities.TryGetValue(violation.RuleId, out var severity) ? violation with { Severity = severity } : violation;

    internal bool IsAllowed(McpContractViolation violation)
        => _allowed.Any(a => string.Equals(a.RuleId, violation.RuleId, StringComparison.OrdinalIgnoreCase)
                             && (a.Target is null || string.Equals(a.Target, violation.Target, StringComparison.Ordinal)));

    private McpContractOptions Add(string tool, object? arguments, bool expectError, string? containing = null)
    {
        if (string.IsNullOrWhiteSpace(tool)) throw new ArgumentException("A tool name is required.", nameof(tool));
        _probes.Add(new McpProbe(tool, McpTestClient.Arguments(arguments), expectError, containing));
        return this;
    }
}

/// <summary>Everything the contract found. Call <see cref="EnsureNoErrors"/> from any test framework.</summary>
public sealed class McpContractReport
{
    public McpContractReport(IReadOnlyList<McpContractViolation> violations)
        => Violations = violations ?? throw new ArgumentNullException(nameof(violations));

    public IReadOnlyList<McpContractViolation> Violations { get; }

    public IReadOnlyList<McpContractViolation> Errors => Violations.Where(v => v.Severity == McpContractSeverity.Error).ToList();

    public IReadOnlyList<McpContractViolation> Warnings => Violations.Where(v => v.Severity == McpContractSeverity.Warning).ToList();

    /// <summary>True when there is no error. Warnings do not count.</summary>
    public bool IsClean => Errors.Count == 0;

    /// <summary>Throws <see cref="McpContractException"/> listing every finding when there is an error.</summary>
    public void EnsureNoErrors()
    {
        if (!IsClean) throw new McpContractException(this);
    }

    public override string ToString()
    {
        if (Violations.Count == 0) return "The MCP contract holds: no errors, no warnings.";

        var text = new StringBuilder();
        text.Append("MCP contract: ").Append(Errors.Count).Append(" error(s), ").Append(Warnings.Count).Append(" warning(s).");
        foreach (var violation in Errors.Concat(Warnings))
            text.AppendLine().Append("  ").Append(violation);
        return text.ToString();
    }
}

/// <summary>Thrown by <see cref="McpContractReport.EnsureNoErrors"/>; the message lists every finding.</summary>
public sealed class McpContractException : Exception
{
    public McpContractException(McpContractReport report)
        : base((report ?? throw new ArgumentNullException(nameof(report))).ToString())
        => Report = report;

    public McpContractReport Report { get; }
}
