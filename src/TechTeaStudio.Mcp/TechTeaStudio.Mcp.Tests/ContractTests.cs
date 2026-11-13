using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TechTeaStudio.Mcp.Testing;
using Xunit;

namespace TechTeaStudio.Mcp.Tests;

public enum Horizon
{
    Day,
    Week,
    Month,
}

[McpServerToolType]
public sealed class GoodTools
{
    [McpServerTool(Name = "find_notes", Title = "Find notes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the person's Notebook notes for a horizon, newest first.")]
    public string FindNotes(
        [Description("Which notes to list. Week when omitted.")] Horizon horizon = Horizon.Week,
        [Description("Keeps notes whose title, description or label contains this text.")] string? text = null,
        [Description("How many notes to return.")][Range(1, 50)] int limit = 10)
        => $"{limit} notes for the {horizon.ToString().ToLowerInvariant()}{(text is null ? "" : " with " + text)}";

    [McpServerTool(Name = "add_note", Title = "Add note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a note to the person's Notebook and returns it.")]
    public string AddNote([Description("The note's text.")][StringLength(200)] string text) => text;
}

[McpServerPromptType]
public sealed class GoodPrompts
{
    [McpServerPrompt(Name = "tidy_notes", Title = "Tidy notes")]
    [Description("Reads the week's Notebook notes and proposes which to merge.")]
    public static string TidyNotes() => "Call find_notes with horizon week, then propose which notes to merge.";
}

[McpServerPromptType]
public sealed class VaguePrompts
{
    [McpServerPrompt(Name = "vague")]
    public static string Vague([Description("")] string topic) => "Do something about " + topic;
}

[McpServerToolType]
public sealed class BadTools
{
    [McpServerTool(Name = "BadName")]
    [Description("Does it.")]
    public string BadName(
        [Description("day, week or month.")] string period,
        int limit,
        [Description("How big, 1 to 50.")] int size)
        => period + limit + size;
}

[McpServerToolType]
public sealed class DataTools
{
    [McpServerTool(Name = "get_usage", Title = "Get usage", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns Notebook usage for a period, or an error object.")]
    public string GetUsage([Description("The period to report.")] string period)
        => period == "today" ? "{\"total\":1}" : "{\"error\":\"Unknown period. Use today.\"}";

    [McpServerTool(Name = "escaped", Title = "Escaped", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns Notebook text serialized with the default encoder.")]
    public string Escaped() => JsonSerializer.Serialize(new { name = "Сеть", tag = "a+b" });

    [McpServerTool(Name = "noisy", Title = "Noisy", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a Notebook speed with too many decimals.")]
    public string Noisy() => "{\"speed\":1396.193419862604}";

    [McpServerTool(Name = "stamp", Title = "Stamp", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a Notebook timestamp with seven fractional digits.")]
    public string Stamp() => "{\"at\":\"2026-11-13T17:58:19.0000000+00:00\"}";

    [McpServerTool(Name = "huge", Title = "Huge", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a Notebook answer far over the budget.")]
    public string Huge() => new('x', 50_000);

    [McpServerTool(Name = "crash", Title = "Crash", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Fails with a Notebook domain rule.")]
    public string Crash() => throw new InvalidOperationException("Only the owner can change this note.");

    [McpServerTool(Name = "explode", Title = "Explode", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Fails unexpectedly inside Notebook.")]
    public string Explode() => throw new NullReferenceException("connection string = secret");

    [McpServerTool(Name = "refuse", Title = "Refuse", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Refuses with a Notebook sentence.")]
    public string Refuse() => throw new McpException("Use a date, e.g. 2026-11-10.");

    [McpServerTool(Name = "row", Title = "Row", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns a Notebook row whose note is null.")]
    public Row GetRow() => new("Привет", null, Shade.Red);
}

public class ContractTests
{
    private const string Instructions =
        "Notebook keeps a person's notes. Start with find_notes to see what is there; add_note writes a new one. Note text is the person's own.";

    /// <summary>For the servers made of DataTools, whose tools the instructions above do not have.</summary>
    private const string DataInstructions =
        "Notebook keeps a person's notes and answers questions about them. Every answer quotes the person's own text.";

    /// <summary>A tool whose description carries a long dash, built at run time so this source file has none.</summary>
    private static McpServerTool DashedTool()
        => McpServerTool.Create(() => "summary", new McpServerToolCreateOptions
        {
            Name = "dashed",
            Title = "Dashed",
            Description = "Returns the Notebook summary " + (char)0x2014 + " with a long dash in it.",
            ReadOnly = true,
            Idempotent = true,
            OpenWorld = false,
        });

    private static Task<McpTestServer> StudioAsync<TTools>(string instructions = Instructions, Action<IMcpServerBuilder>? more = null)
        where TTools : class
        => McpTestServer.StartAsync(s =>
        {
            var builder = s
                .AddStudioMcp(o =>
                {
                    o.Name = "notebook";
                    o.Title = "Notebook";
                    o.Instructions = instructions;
                    o.IsRefusal = ex => ex is InvalidOperationException;
                })
                .WithStudioTools<TTools>();
            more?.Invoke(builder);
        });

    private static Task<McpTestServer> BareAsync<TTools>()
        where TTools : class
        => McpTestServer.StartAsync(s => s
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "bare", Version = "1.0" })
            .WithTools<TTools>());

    private static (string Rule, string Target, McpContractSeverity Severity)[] Found(McpContractReport report)
        => report.Violations.Select(v => (v.RuleId, v.Target, v.Severity)).ToArray();

    [Fact]
    public async Task A_well_made_studio_server_is_clean()
    {
        await using var server = await StudioAsync<GoodTools>(more: b => b.WithStudioPrompts<GoodPrompts>());

        var report = await McpContract.CheckAsync(server.Client, new McpContractOptions { ProductName = "Notebook" }
            .Probe("find_notes", new { horizon = "week", limit = 5 })
            .ProbeError("find_notes", new { horizon = "year" }, containing: "day, week, month")
            .ProbeError("add_note", new { text = new string('a', 300) }, containing: "maximum of 200"));

        Assert.Empty(report.Violations);
        report.EnsureNoErrors();
    }

    [Fact]
    public async Task A_bare_sdk_server_has_no_title_and_no_instructions()
    {
        await using var server = await BareAsync<GoodTools>();

        var report = await McpContract.CheckAsync(server.Client, new McpContractOptions { ProductName = "Notebook" });

        Assert.Contains(("TTS-MCP-001", "server", McpContractSeverity.Error), Found(report));
        Assert.Contains(("TTS-MCP-002", "server", McpContractSeverity.Error), Found(report));
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task Instructions_that_never_name_the_product_are_an_error()
    {
        await using var server = await StudioAsync<GoodTools>("Keeps notes. Start with find_notes to see what is there; add_note writes a new one. Text is the person's own.");

        var report = await McpContract.CheckAsync(server.Client, new McpContractOptions { ProductName = "Notebook" });

        Assert.Equal(new[] { ("TTS-MCP-004", "server", McpContractSeverity.Error) }, Found(report));
    }

    [Fact]
    public async Task Instructions_that_name_a_tool_the_server_does_not_have_are_flagged()
    {
        await using var server = await StudioAsync<GoodTools>(Instructions + " Old notes go with delete_note.");

        var report = await McpContract.CheckAsync(server.Client, new McpContractOptions { ProductName = "Notebook" });

        var finding = Assert.Single(report.Violations);
        Assert.Equal(("TTS-MCP-026", McpContractSeverity.Warning), (finding.RuleId, finding.Severity));
        Assert.Contains("delete_note", finding.Message);
    }

    [Fact]
    public async Task Tool_rules_catch_what_makes_a_model_guess()
    {
        await using var server = await StudioAsync<BadTools>(more: b => b.WithTools(new[] { DashedTool() }));

        var found = Found(await McpContract.CheckAsync(server.Client, new McpContractOptions { ProductName = "Notebook" }));

        Assert.Contains(("TTS-MCP-007", "BadName", McpContractSeverity.Warning), found);
        Assert.Contains(("TTS-MCP-008", "BadName", McpContractSeverity.Error), found);
        Assert.Contains(("TTS-MCP-009", "BadName", McpContractSeverity.Error), found);
        Assert.Contains(("TTS-MCP-010", "BadName", McpContractSeverity.Warning), found);
        Assert.Contains(("TTS-MCP-011", "BadName", McpContractSeverity.Error), found);
        Assert.Contains(("TTS-MCP-013", "BadName.limit", McpContractSeverity.Error), found);
        Assert.Contains(("TTS-MCP-014", "BadName.period", McpContractSeverity.Error), found);
        Assert.Contains(("TTS-MCP-015", "BadName.size", McpContractSeverity.Warning), found);
        Assert.Contains(("TTS-MCP-016", "dashed", McpContractSeverity.Error), found);
        Assert.DoesNotContain(found, f => f.Target == "dashed" && f.Rule != "TTS-MCP-016");
    }

    [Fact]
    public async Task Allow_waives_a_rule_and_Severity_moves_one()
    {
        await using var server = await StudioAsync<BadTools>(more: b => b.WithTools(new[] { DashedTool() }));

        var found = Found(await McpContract.CheckAsync(server.Client, new McpContractOptions()
            .Allow("TTS-MCP-014", "BadName.period")
            .Severity("TTS-MCP-016", McpContractSeverity.Warning)));

        Assert.DoesNotContain(found, f => f.Rule == "TTS-MCP-014");
        Assert.Contains(("TTS-MCP-016", "dashed", McpContractSeverity.Warning), found);
        Assert.Contains(found, f => f.Rule == "TTS-MCP-013");
    }

    [Fact]
    public async Task Prompts_need_descriptions()
    {
        await using var server = await StudioAsync<GoodTools>(more: b => b.WithStudioPrompts<VaguePrompts>());

        var found = Found(await McpContract.CheckAsync(server.Client, new McpContractOptions { ProductName = "Notebook" }));

        Assert.Equal(
            new[]
            {
                ("TTS-MCP-024", "prompt vague", McpContractSeverity.Error),
                ("TTS-MCP-024", "prompt vague.topic", McpContractSeverity.Warning),
            },
            found);
    }

    [Fact]
    public async Task A_tool_list_over_its_budget_is_a_warning()
    {
        await using var server = await StudioAsync<GoodTools>();

        var found = Found(await McpContract.CheckAsync(server.Client, new McpContractOptions { MaxToolListChars = 100 }));

        Assert.Equal(new[] { ("TTS-MCP-025", "server", McpContractSeverity.Warning) }, found);
    }

    [Fact]
    public async Task Probes_catch_refusals_sent_as_data_escaped_text_noise_and_size_but_not_timestamps()
    {
        await using var server = await StudioAsync<DataTools>(DataInstructions);

        var found = Found(await McpContract.CheckAsync(server.Client, new McpContractOptions()
            .ProbeError("get_usage", new { period = "next tuesday" })
            .Probe("get_usage", new { period = "today" })
            .Probe("escaped")
            .Probe("noisy")
            .Probe("stamp")
            .Probe("huge")));

        Assert.Equal(
            new[]
            {
                ("TTS-MCP-017", "get_usage", McpContractSeverity.Error),
                ("TTS-MCP-020", "escaped", McpContractSeverity.Error),
                ("TTS-MCP-021", "noisy", McpContractSeverity.Warning),
                ("TTS-MCP-019", "huge", McpContractSeverity.Error),
            },
            found);
    }

    [Fact]
    public async Task A_crash_or_the_wrong_words_do_not_pass_for_a_refusal()
    {
        await using var server = await StudioAsync<DataTools>(DataInstructions);

        var found = Found(await McpContract.CheckAsync(server.Client, new McpContractOptions()
            .ProbeError("explode")
            .ProbeError("refuse", containing: "a date")
            .ProbeError("crash", containing: "a date")));

        Assert.Equal(
            new[]
            {
                ("TTS-MCP-017", "explode", McpContractSeverity.Error),
                ("TTS-MCP-017", "crash", McpContractSeverity.Error),
            },
            found);
    }

    [Fact]
    public async Task The_sdk_error_wrapper_is_flagged_on_a_bare_server_and_gone_on_a_studio_one()
    {
        var options = new McpContractOptions().ProbeError("crash").ProbeError("refuse")
            .Allow("TTS-MCP-001").Allow("TTS-MCP-002");

        await using var bare = await BareAsync<DataTools>();
        await using var studio = await StudioAsync<DataTools>(DataInstructions);
        var onBare = Found(await McpContract.CheckAsync(bare.Client, options));
        var onStudio = Found(await McpContract.CheckAsync(studio.Client, options));

        Assert.Equal(
            new[] { ("TTS-MCP-018", "crash", McpContractSeverity.Error), ("TTS-MCP-018", "refuse", McpContractSeverity.Warning) },
            onBare);
        Assert.Empty(onStudio);
    }

    [Fact]
    public async Task Structured_content_that_breaks_its_output_schema_is_flagged_on_a_bare_server_only()
    {
        var options = new McpContractOptions().Probe("row").Allow("TTS-MCP-001").Allow("TTS-MCP-002");

        await using var bare = await BareAsync<DataTools>();
        await using var studio = await StudioAsync<DataTools>(DataInstructions);
        var onBare = Found(await McpContract.CheckAsync(bare.Client, options));
        var onStudio = Found(await McpContract.CheckAsync(studio.Client, options));

        // The bare SDK also escapes the Cyrillic title in the text block: two findings, one call.
        Assert.Equal(
            new[] { ("TTS-MCP-020", "row", McpContractSeverity.Error), ("TTS-MCP-022", "row", McpContractSeverity.Error) },
            onBare);
        Assert.Empty(onStudio);
    }

    [Fact]
    public async Task A_probe_of_a_tool_the_server_does_not_list_is_an_error()
    {
        await using var server = await StudioAsync<GoodTools>();

        var found = Found(await McpContract.CheckAsync(server.Client, new McpContractOptions().Probe("no_such_tool")));

        Assert.Equal(new[] { ("TTS-MCP-023", "no_such_tool", McpContractSeverity.Error) }, found);
    }

    [Fact]
    public void EnsureNoErrors_lists_every_finding_and_ignores_warnings_alone()
    {
        var warningsOnly = new McpContractReport(new[] { new McpContractViolation("TTS-MCP-010", McpContractSeverity.Warning, "x", "openWorldHint is not stated.") });
        var withError = new McpContractReport(new[]
        {
            new McpContractViolation("TTS-MCP-010", McpContractSeverity.Warning, "x", "openWorldHint is not stated."),
            new McpContractViolation("TTS-MCP-008", McpContractSeverity.Error, "x", "The tool has no title."),
        });

        warningsOnly.EnsureNoErrors();
        var error = Assert.Throws<McpContractException>(withError.EnsureNoErrors);

        Assert.Contains("TTS-MCP-008 error [x] The tool has no title.", error.Message);
        Assert.Contains("TTS-MCP-010 warning [x]", error.Message);
        Assert.StartsWith("MCP contract: 1 error(s), 1 warning(s).", error.Message);
    }
}
