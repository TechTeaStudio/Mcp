using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TechTeaStudio.Mcp.Testing;

/// <summary>
/// Checks a server the way a model meets it: from the client side, through what the server says about itself,
/// what <c>tools/list</c> and <c>prompts/list</c> return, and the probe calls. Each rule names a mistake that
/// makes a model guess, fail or waste tokens. The rules and their ids:
/// <list type="table">
/// <item><term>TTS-MCP-001</term><description>error: the server sends no title.</description></item>
/// <item><term>TTS-MCP-002</term><description>error: no instructions, or shorter than <see cref="McpContractOptions.MinInstructionsLength"/>.</description></item>
/// <item><term>TTS-MCP-003</term><description>warning: instructions longer than <see cref="McpContractOptions.MaxInstructionsLength"/>; they ride along in every conversation.</description></item>
/// <item><term>TTS-MCP-004</term><description>error: the instructions do not name <see cref="McpContractOptions.ProductName"/>.</description></item>
/// <item><term>TTS-MCP-005</term><description>warning: no tool title or description names the product.</description></item>
/// <item><term>TTS-MCP-006</term><description>error: a tool name breaks the MCP format (1 to 64 of A-Z, a-z, 0-9, _, -, .).</description></item>
/// <item><term>TTS-MCP-007</term><description>warning: a tool name is not snake_case.</description></item>
/// <item><term>TTS-MCP-008</term><description>error: a tool has no title.</description></item>
/// <item><term>TTS-MCP-009</term><description>error: readOnlyHint is not stated, or a writing tool does not state destructiveHint and idempotentHint.</description></item>
/// <item><term>TTS-MCP-010</term><description>warning: openWorldHint is not stated.</description></item>
/// <item><term>TTS-MCP-011</term><description>error: a tool description is missing or shorter than <see cref="McpContractOptions.MinDescriptionLength"/>.</description></item>
/// <item><term>TTS-MCP-012</term><description>warning: a tool description is longer than <see cref="McpContractOptions.MaxDescriptionLength"/>.</description></item>
/// <item><term>TTS-MCP-013</term><description>error: a parameter has no description.</description></item>
/// <item><term>TTS-MCP-014</term><description>error: a text parameter's description opens with its allowed values ("a, b or c") instead of an enum in the schema.</description></item>
/// <item><term>TTS-MCP-015</term><description>warning: a number parameter states its bounds in prose ("1 to 50") instead of minimum and maximum.</description></item>
/// <item><term>TTS-MCP-016</term><description>error (studio rule): a long dash (U+2013, U+2014) in a title, a description or the instructions.</description></item>
/// <item><term>TTS-MCP-017</term><description>error: a probe got an answer where it expected a refusal (a refusal sent as data), a crash instead of a refusal, a refusal without the expected words, or an error where it expected an answer.</description></item>
/// <item><term>TTS-MCP-018</term><description>error: an error is the SDK's bare "An error occurred invoking" with nothing to act on; warning when that wrapper only prefixes the real message.</description></item>
/// <item><term>TTS-MCP-019</term><description>error: an answer is longer than <see cref="McpContractOptions.MaxResultChars"/>.</description></item>
/// <item><term>TTS-MCP-020</term><description>error: an answer carries \u escapes of non-Latin or HTML-sensitive characters.</description></item>
/// <item><term>TTS-MCP-021</term><description>warning: an answer has numbers with five or more decimal places.</description></item>
/// <item><term>TTS-MCP-022</term><description>error: structured content leaves out a property its output schema requires.</description></item>
/// <item><term>TTS-MCP-023</term><description>error: a probe names a tool the server does not list.</description></item>
/// <item><term>TTS-MCP-024</term><description>error: a prompt has no description; warning: a prompt argument has no description.</description></item>
/// <item><term>TTS-MCP-025</term><description>warning: <c>tools/list</c> is longer than <see cref="McpContractOptions.MaxToolListChars"/>.</description></item>
/// <item><term>TTS-MCP-026</term><description>warning: the instructions name a snake_case identifier that is no tool, prompt, parameter or output field.</description></item>
/// </list>
/// Every rule can be waived (<see cref="McpContractOptions.Allow"/>) or given another severity (<see cref="McpContractOptions.Severity"/>).
/// </summary>
public static class McpContract
{
    private static readonly Regex ToolNameFormat = new(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex SnakeCase = new(@"^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    // Anchored to the start of the description or to text after a colon: "day, week or month." and "Keeps
    // tasks of one priority: none, blaze or surge." are value lists, "the title, description or label" is prose.
    private static readonly Regex ProseList = new(@"(?:^\s*|:\s*)[a-z0-9_]+(?:, [a-z0-9_]+)+,? or [a-z0-9_]+(?![\w\-])", RegexOptions.CultureInvariant);
    private static readonly Regex ProsePair = new(@"^\s*[a-z0-9_]+ or [a-z0-9_]+(?![\w\-])", RegexOptions.CultureInvariant);
    private static readonly Regex ProseRange = new(@"\b\d+ to \d+\b|\bbetween \d+ and \d+\b|\bup to \d+\b|\bat most \d+\b|\bat least \d+\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Escape = new(@"\\u([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant);
    // Not after "digit colon": the seconds of a timestamp (12:30:19.0000000) are not a noisy measurement, while a
    // JSON value ("speed":1396.193419) sits after a colon that follows a quote.
    private static readonly Regex LongDecimal = new(@"(?<![\w.])(?<!\d:)-?\d+\.\d{5,}(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b", RegexOptions.CultureInvariant);
    private static readonly int[] HtmlSensitive = { 0x26, 0x27, 0x2B, 0x3C, 0x3E, 0x60 };
    private static readonly char[] LongDashes = { (char)0x2014, (char)0x2013 };

    /// <summary>Runs every rule against the server <paramref name="client"/> is connected to.</summary>
    public static async Task<McpContractReport> CheckAsync(McpClient client, McpContractOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        options ??= new McpContractOptions();
        var found = new List<McpContractViolation>();

        CheckServer(client, options, found);

        var tools = (await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Select(t => t.ProtocolTool)
            .ToList();
        foreach (var tool in tools) CheckTool(tool, options, found);

        if (!string.IsNullOrWhiteSpace(options.ProductName) && tools.Count > 0
            && !tools.Any(t => Names(t.Title, options.ProductName!) || Names(t.Description, options.ProductName!) || Names(t.Annotations?.Title, options.ProductName!)))
            found.Add(Warn("TTS-MCP-005", "server", $"No tool title or description says \"{options.ProductName}\". A client that shows the model no instructions leaves it nothing to tell this server's tools from another's."));

        var listChars = tools.Sum(t => JsonSerializer.Serialize(t, McpJsonUtilities.DefaultOptions).Length);
        if (listChars > options.MaxToolListChars)
            found.Add(Warn("TTS-MCP-025", "server", $"tools/list is {listChars} characters of JSON, over {options.MaxToolListChars}; every definition rides along in the model's context. Merge tools or shorten the descriptions."));

        var prompts = await ListPromptsAsync(client, cancellationToken).ConfigureAwait(false);
        foreach (var prompt in prompts) CheckPrompt(prompt, options, found);

        CheckNamedIdentifiers(client.ServerInstructions, tools, prompts, found);

        foreach (var probe in options.Probes)
            await CheckProbeAsync(client, tools, probe, options, found, cancellationToken).ConfigureAwait(false);

        return new McpContractReport(found.Select(options.Apply).Where(v => !options.IsAllowed(v)).ToList());
    }

    private static void CheckServer(McpClient client, McpContractOptions options, List<McpContractViolation> found)
    {
        var info = client.ServerInfo;
        if (string.IsNullOrWhiteSpace(info?.Title))
            found.Add(Fail("TTS-MCP-001", "server", "The server sends no title. Clients show it to people; set it next to the name."));
        else
            CheckDashes(info!.Title!, "server", "The server title", found);

        var instructions = client.ServerInstructions;
        if (string.IsNullOrWhiteSpace(instructions) || instructions!.Trim().Length < options.MinInstructionsLength)
        {
            found.Add(Fail("TTS-MCP-002", "server",
                $"The server sends {(string.IsNullOrWhiteSpace(instructions) ? "no instructions" : $"only {instructions!.Trim().Length} characters of instructions")}. Say what the product is, the words its tools use and where to start (at least {options.MinInstructionsLength} characters)."));
            return;
        }

        if (instructions.Length > options.MaxInstructionsLength)
            found.Add(Warn("TTS-MCP-003", "server", $"The instructions are {instructions.Length} characters; they ride along in every conversation. Keep them under {options.MaxInstructionsLength}."));
        if (!string.IsNullOrWhiteSpace(options.ProductName) && !Names(instructions, options.ProductName!))
            found.Add(Fail("TTS-MCP-004", "server", $"The instructions never say \"{options.ProductName}\", so the model cannot tell which connected server is this product."));
        CheckDashes(instructions, "server", "The instructions", found);
    }

    private static void CheckTool(Tool tool, McpContractOptions options, List<McpContractViolation> found)
    {
        var name = tool.Name ?? string.Empty;
        if (!ToolNameFormat.IsMatch(name))
            found.Add(Fail("TTS-MCP-006", name, "The tool name must be 1 to 64 characters of A-Z, a-z, 0-9, underscore, hyphen or dot."));
        else if (!SnakeCase.IsMatch(name))
            found.Add(Warn("TTS-MCP-007", name, "The tool name is not snake_case, the studio convention (find_tasks, get_status)."));

        var title = string.IsNullOrWhiteSpace(tool.Title) ? tool.Annotations?.Title : tool.Title;
        if (string.IsNullOrWhiteSpace(title))
            found.Add(Fail("TTS-MCP-008", name, "The tool has no title. Clients show it when they ask the person to approve a call."));
        else
            CheckDashes(title!, name, "The title", found);

        var hints = tool.Annotations;
        if (hints?.ReadOnlyHint is null)
            found.Add(Fail("TTS-MCP-009", name, "readOnlyHint is not stated. Clients decide from it whether to ask before a call."));
        else if (hints.ReadOnlyHint == false && (hints.DestructiveHint is null || hints.IdempotentHint is null))
            found.Add(Fail("TTS-MCP-009", name, "A writing tool must state destructiveHint and idempotentHint; left out, clients assume the worst."));
        if (hints?.OpenWorldHint is null)
            found.Add(Warn("TTS-MCP-010", name, "openWorldHint is not stated."));

        var description = tool.Description?.Trim() ?? string.Empty;
        if (description.Length < options.MinDescriptionLength)
            found.Add(Fail("TTS-MCP-011", name, $"The description is {(description.Length == 0 ? "missing" : $"{description.Length} characters")}. Say what the tool does, what it returns and when to use it."));
        else if (description.Length > options.MaxDescriptionLength)
            found.Add(Warn("TTS-MCP-012", name, $"The description is {description.Length} characters; some clients cut it at {options.MaxDescriptionLength}."));
        CheckDashes(description, name, "The description", found);

        CheckParameters(tool.InputSchema, name, found);
    }

    private static void CheckParameters(JsonElement schema, string prefix, List<McpContractViolation> found)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            var target = $"{prefix}.{property.Name}";
            var parameter = property.Value;
            var description = parameter.ValueKind == JsonValueKind.Object && parameter.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()!.Trim()
                : string.Empty;

            if (description.Length == 0)
                found.Add(Fail("TTS-MCP-013", target, "The parameter has no description. Say what it means, its format and its default."));
            else
                CheckDashes(description, target, "The parameter description", found);

            var types = TypesOf(parameter);
            var items = parameter.ValueKind == JsonValueKind.Object && parameter.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
            var itemTypes = items.ValueKind == JsonValueKind.Object ? TypesOf(items) : new HashSet<string>();

            var textValues = (types.Contains("string") && !HasEnum(parameter))
                             || (types.Contains("array") && itemTypes.Contains("string") && !HasEnum(items));
            if (textValues && (ProseList.IsMatch(description) || ProsePair.IsMatch(description)))
                found.Add(Fail("TTS-MCP-014", target, $"The allowed values are written in the description (\"{Snippet(description)}\") but the schema has no enum. Make the parameter an enum, so the schema lists them and a wrong one is refused with the list."));

            if ((types.Contains("integer") || types.Contains("number"))
                && !parameter.TryGetProperty("minimum", out _) && !parameter.TryGetProperty("maximum", out _)
                && ProseRange.IsMatch(description))
                found.Add(Warn("TTS-MCP-015", target, $"The bounds are written in the description (\"{Snippet(description)}\") but the schema has no minimum or maximum. Put them in the schema ([Range] on the parameter)."));

            if (types.Contains("object"))
                CheckParameters(parameter, target, found);
            else if (items.ValueKind == JsonValueKind.Object && itemTypes.Contains("object"))
                CheckParameters(items, $"{target}[]", found);
        }
    }

    private static async Task<IReadOnlyList<Prompt>> ListPromptsAsync(McpClient client, CancellationToken cancellationToken)
    {
        if (client.ServerCapabilities?.Prompts is null) return Array.Empty<Prompt>();
        return (await client.ListPromptsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Select(p => p.ProtocolPrompt)
            .ToList();
    }

    private static void CheckPrompt(Prompt prompt, McpContractOptions options, List<McpContractViolation> found)
    {
        var target = "prompt " + prompt.Name;
        var description = prompt.Description?.Trim() ?? string.Empty;
        if (description.Length < options.MinDescriptionLength)
            found.Add(Fail("TTS-MCP-024", target, $"The prompt's description is {(description.Length == 0 ? "missing" : $"{description.Length} characters")}. Clients list prompts by it; say what the prompt does."));
        else
            CheckDashes(description, target, "The prompt description", found);

        foreach (var argument in prompt.Arguments ?? new List<PromptArgument>())
        {
            if (string.IsNullOrWhiteSpace(argument.Description))
                found.Add(Warn("TTS-MCP-024", $"{target}.{argument.Name}", "The prompt argument has no description."));
        }
    }

    private static void CheckNamedIdentifiers(string? instructions, IReadOnlyList<Tool> tools, IReadOnlyList<Prompt> prompts, List<McpContractViolation> found)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return;

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            known.Add(tool.Name);
            CollectNames(tool.InputSchema, known);
            if (tool.OutputSchema is { } output) CollectNames(output, known);
        }

        foreach (var prompt in prompts)
        {
            known.Add(prompt.Name);
            foreach (var argument in prompt.Arguments ?? new List<PromptArgument>()) known.Add(argument.Name);
        }

        var unknown = Identifier.Matches(instructions!).Cast<Match>().Select(m => m.Value).Where(v => !known.Contains(v)).Distinct().ToList();
        if (unknown.Count > 0)
            found.Add(Warn("TTS-MCP-026", "server", $"The instructions name {string.Join(", ", unknown)}, which no tool, prompt, parameter or output field is called. A renamed tool leaves its old name here."));
    }

    private static void CollectNames(JsonElement schema, HashSet<string> names)
    {
        switch (schema.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in schema.EnumerateObject())
                {
                    if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
                        foreach (var field in property.Value.EnumerateObject()) names.Add(field.Name);
                    CollectNames(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in schema.EnumerateArray()) CollectNames(item, names);
                break;
        }
    }

    private static async Task CheckProbeAsync(
        McpClient client, List<Tool> tools, McpProbe probe, McpContractOptions options, List<McpContractViolation> found, CancellationToken cancellationToken)
    {
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, probe.Tool, StringComparison.Ordinal));
        if (tool is null)
        {
            found.Add(Fail("TTS-MCP-023", probe.Tool, "A probe calls this tool, but the server does not list it."));
            return;
        }

        var result = await client.CallToolAsync(probe.Tool, probe.Arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = McpTestClient.Text(result);
        var isError = result.IsError == true;

        if (probe.ExpectError && !isError)
            found.Add(Fail("TTS-MCP-017", probe.Tool, $"The call should have been refused but came back as an answer: \"{Snippet(text)}\". Report a refusal as an error (throw McpException), so the client and the model see it failed."));
        else if (probe.ExpectError && options.FailureMessages.Any(m => string.Equals(m.Trim(), text.Trim(), StringComparison.Ordinal)))
            found.Add(Fail("TTS-MCP-017", probe.Tool, "The call crashed instead of being refused: the answer is the generic failure sentence. Refuse it with a sentence that says what to change."));
        else if (probe.ExpectError && probe.ExpectedText is { } expected && text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
            found.Add(Fail("TTS-MCP-017", probe.Tool, $"The refusal does not say \"{expected}\": \"{Snippet(text)}\"."));
        else if (!probe.ExpectError && isError)
            found.Add(Fail("TTS-MCP-017", probe.Tool, $"The call failed: \"{Snippet(text)}\"."));

        if (isError)
        {
            var bare = $"An error occurred invoking '{probe.Tool}'.";
            var prefix = $"An error occurred invoking '{probe.Tool}': ";
            if (string.Equals(text.Trim(), bare, StringComparison.Ordinal))
                found.Add(Fail("TTS-MCP-018", probe.Tool, "The error is the SDK's bare wrapper; the model learns nothing it can act on. Throw McpException with a sentence that says what to change."));
            else if (text.StartsWith(prefix, StringComparison.Ordinal))
                found.Add(Warn("TTS-MCP-018", probe.Tool, "The SDK's \"An error occurred invoking\" prefix comes before the real message; the studio pipeline sends the message alone."));
        }

        if (text.Length > options.MaxResultChars)
            found.Add(Fail("TTS-MCP-019", probe.Tool, $"The answer is {text.Length} characters, over the budget of {options.MaxResultChars}. Cap the rows or narrow the default period."));

        var escapes = Escape.Matches(text).Cast<Match>()
            .Select(m => (m.Value, Code: int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
            .Where(e => e.Code > 0x7F || HtmlSensitive.Contains(e.Code))
            .ToList();
        if (escapes.Count > 0)
            found.Add(Fail("TTS-MCP-020", probe.Tool, $"The answer has {escapes.Count} \\u escape(s), e.g. {escapes[0].Value} for '{(char)escapes[0].Code}'. Serialize with a relaxed encoder (StudioMcpJson) so text stays readable and cheap."));

        var decimals = LongDecimal.Match(text);
        if (decimals.Success)
            found.Add(Warn("TTS-MCP-021", probe.Tool, $"The answer has numbers like {decimals.Value}. Round them: the digits cost tokens and mean nothing to a model."));

        if (!isError && tool.OutputSchema is { } schema && result.StructuredContent is { } structured && structured.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            var missing = required.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String && !structured.TryGetProperty(r.GetString()!, out _))
                .Select(r => r.GetString()!)
                .ToList();
            if (missing.Count > 0)
                found.Add(Fail("TTS-MCP-022", probe.Tool, $"The structured content has no {string.Join(", ", missing)}, which its output schema requires. A client that validates the answer rejects it; leave nullable properties out of \"required\" (AddStudioMcp does)."));
        }
    }

    private static void CheckDashes(string text, string target, string what, List<McpContractViolation> found)
    {
        var at = text.IndexOfAny(LongDashes);
        if (at >= 0)
            found.Add(Fail("TTS-MCP-016", target, $"{what} has a long dash at character {at + 1} (\"{Snippet(text.Substring(Math.Max(0, at - 20)))}\"). Use a plain hyphen."));
    }

    private static HashSet<string> TypesOf(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type)) return types;
        if (type.ValueKind == JsonValueKind.String) types.Add(type.GetString()!);
        else if (type.ValueKind == JsonValueKind.Array)
            foreach (var entry in type.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String) types.Add(entry.GetString()!);
        return types;
    }

    private static bool HasEnum(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array;

    private static bool Names(string? text, string product)
        => !string.IsNullOrEmpty(text) && text!.IndexOf(product, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Snippet(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length > 80 ? flat.Substring(0, 80) + "..." : flat;
    }

    private static McpContractViolation Fail(string rule, string target, string message) => new(rule, McpContractSeverity.Error, target, message);

    private static McpContractViolation Warn(string rule, string target, string message) => new(rule, McpContractSeverity.Warning, target, message);
}
