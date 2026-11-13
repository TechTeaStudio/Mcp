using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TechTeaStudio.Mcp.Testing;
using Xunit;

namespace TechTeaStudio.Mcp.Tests;

public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class CollectingObserver : IMcpCallObserver
{
    private readonly List<McpCallRecord> _calls = new();

    public IReadOnlyList<McpCallRecord> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public ValueTask OnCallCompletedAsync(McpCallRecord call, CancellationToken cancellationToken)
    {
        lock (_calls) _calls.Add(call);
        return default;
    }
}

public sealed class ThrowingObserver : IMcpCallObserver
{
    public ValueTask OnCallCompletedAsync(McpCallRecord call, CancellationToken cancellationToken)
        => throw new InvalidOperationException("observer is broken");
}

public sealed class TimingOutObserver : IMcpCallObserver
{
    public ValueTask OnCallCompletedAsync(McpCallRecord call, CancellationToken cancellationToken)
        => throw new TaskCanceledException("the journal's own timeout");
}

[McpServerToolType]
public sealed class MeteredTools
{
    [McpServerTool(Name = "metered_ping", Title = "Metered ping", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [System.ComponentModel.Description("Answers pong for the metrics test.")]
    public string Ping() => "pong";
}

public class PipelineTests
{
    internal const string Instructions = "Sample is a test server. Its tools echo what they are given.";

    internal static Task<McpTestServer> StartAsync(Action<StudioMcpOptions>? tune = null, Action<IServiceCollection>? services = null)
        => McpTestServer.StartAsync(s =>
        {
            s.AddSingleton<RunCounter>();
            services?.Invoke(s);
            s.AddStudioMcp(o =>
            {
                o.Name = "sample";
                o.Title = "Sample";
                o.Instructions = Instructions;
                o.IsRefusal = ex => ex is InvalidOperationException;
                tune?.Invoke(o);
            }).WithStudioTools<SampleTools>();
        });

    private static int Runs(McpTestServer server) => server.Services.GetRequiredService<RunCounter>().Runs;

    [Fact]
    public async Task Identity_and_instructions_reach_the_client()
    {
        await using var server = await StartAsync(o => o.WebsiteUrl = "https://example.org");

        var info = server.Client.ServerInfo;
        Assert.NotNull(info);
        Assert.Equal("sample", info!.Name);
        Assert.Equal("Sample", info.Title);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.Equal("https://example.org", info.WebsiteUrl);
        Assert.Equal(Instructions, server.Client.ServerInstructions);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("title")]
    [InlineData("instructions")]
    public void Registration_without_a_required_field_fails_at_startup(string missing)
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() => services.AddStudioMcp(o =>
        {
            o.Name = missing == "name" ? " " : "sample";
            o.Title = missing == "title" ? null : "Sample";
            o.Instructions = missing == "instructions" ? "" : Instructions;
        }));

        Assert.Contains(missing, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Studio_tools_need_AddStudioMcp_first()
    {
        var builder = new ServiceCollection().AddMcpServer();

        Assert.Throws<InvalidOperationException>(() => builder.WithStudioTools<SampleTools>());
    }

    [Fact]
    public async Task A_refusal_reaches_the_model_without_the_sdk_prefix()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("refuse");

        Assert.True(result.IsError);
        Assert.Equal("Pick a date, e.g. 2026-11-10.", McpTestClient.Text(result));
    }

    [Fact]
    public async Task An_exception_IsRefusal_accepts_is_a_refusal_with_its_own_message()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("domain_refuse");

        Assert.True(result.IsError);
        Assert.Equal("Only the owner can change this note.", McpTestClient.Text(result));
    }

    [Fact]
    public async Task An_unexpected_exception_answers_the_failure_message_and_keeps_its_details()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("crash");

        Assert.True(result.IsError);
        Assert.Equal("Something went wrong on our side. Try again in a moment.", McpTestClient.Text(result));
        Assert.DoesNotContain("secret", McpTestClient.Text(result));
    }

    [Fact]
    public async Task Enum_parameters_list_their_snake_case_values_and_bind_them()
    {
        await using var server = await StartAsync();

        var tool = (await server.Client.ListToolsAsync()).Single(t => t.Name == "echo_shade");
        var values = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("shade").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()).ToArray();
        var result = await server.CallAsync("echo_shade", new { shade = "dark_blue" });

        Assert.Equal(new[] { "red", "dark_blue" }, values);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal("DarkBlue", McpTestClient.Text(result));
    }

    [Fact]
    public async Task An_unknown_enum_value_is_refused_with_the_allowed_values_before_the_tool_runs()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("echo_shade", new { shade = "purple" });

        Assert.True(result.IsError);
        Assert.Equal("shade: 'purple' is not one of red, dark_blue.", McpTestClient.Text(result));
        Assert.Equal(0, Runs(server));
    }

    [Fact]
    public async Task A_value_above_the_maximum_is_refused_before_the_tool_runs()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("ranged", new { limit = 500 });

        Assert.True(result.IsError);
        Assert.Equal("limit: 500 is above the maximum of 50.", McpTestClient.Text(result));
        Assert.Equal(0, Runs(server));
    }

    [Fact]
    public async Task A_missing_required_argument_is_refused()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("ranged");

        Assert.True(result.IsError);
        Assert.Equal("limit is required.", McpTestClient.Text(result));
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_is_refused_but_a_number_in_a_string_passes()
    {
        await using var server = await StartAsync();

        var wrong = await server.CallAsync("ranged", new { limit = "ten" });
        var quoted = await server.CallAsync("ranged", new { limit = "7" });

        Assert.True(wrong.IsError);
        Assert.Equal("limit must be an integer.", McpTestClient.Text(wrong));
        Assert.NotEqual(true, quoted.IsError);
        Assert.Equal("7", McpTestClient.Text(quoted));
    }

    [Fact]
    public async Task A_string_over_its_maximum_length_is_refused()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("create_note", new { title = new string('a', 25) });

        Assert.True(result.IsError);
        Assert.Equal("title: 25 characters, more than the maximum of 20.", McpTestClient.Text(result));
    }

    [Fact]
    public async Task Items_of_an_array_argument_are_checked_with_their_position()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("create_notes", new
        {
            notes = new object[] { new { title = "ok" }, new { shade = "purple" } },
        });

        Assert.True(result.IsError);
        Assert.Equal("notes[1].title is required. notes[1].shade: 'purple' is not one of red, dark_blue.", McpTestClient.Text(result));
        Assert.Equal(0, Runs(server));
    }

    [Fact]
    public async Task Argument_checks_can_be_switched_off()
    {
        await using var server = await StartAsync(o => o.ValidateArguments = false);

        var result = await server.CallAsync("ranged", new { limit = 500 });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("500", McpTestClient.Text(result));
    }

    [Fact]
    public async Task Cyrillic_stays_readable_in_text_and_structured_answers()
    {
        await using var server = await StartAsync();

        var row = McpTestClient.Text(await server.CallAsync("cyrillic_row"));
        var text = McpTestClient.Text(await server.CallAsync("cyrillic_text"));

        Assert.Contains("Привет", row);
        Assert.Contains("dark_blue", row);
        Assert.DoesNotContain("missing", row);
        Assert.DoesNotContain("\\u", row);
        Assert.Contains("Сеть", text);
    }

    [Fact]
    public async Task The_output_schema_does_not_require_fields_that_are_left_out_when_null()
    {
        await using var server = await StartAsync();

        var tool = (await server.Client.ListToolsAsync()).Single(t => t.Name == "cyrillic_row");
        var required = tool.ProtocolTool.OutputSchema!.Value.GetProperty("required")
            .EnumerateArray().Select(v => v.GetString()).ToArray();

        Assert.Contains("title", required);
        Assert.Contains("shade", required);
        Assert.DoesNotContain("missing", required);
    }

    [Fact]
    public async Task An_answer_over_budget_is_refused_when_configured()
    {
        await using var server = await StartAsync(o =>
        {
            o.MaxResultChars = 1000;
            o.OverBudget = OverBudgetBehavior.Refuse;
        });

        var big = await server.CallAsync("big", new { chars = 5000 });
        var small = await server.CallAsync("big", new { chars = 500 });

        Assert.True(big.IsError);
        Assert.Contains("5000", McpTestClient.Text(big));
        Assert.Contains("1000", McpTestClient.Text(big));
        Assert.NotEqual(true, small.IsError);
    }

    [Fact]
    public async Task An_answer_over_budget_only_warns_by_default()
    {
        await using var server = await StartAsync(o => o.MaxResultChars = 1000);

        var big = await server.CallAsync("big", new { chars = 5000 });

        Assert.NotEqual(true, big.IsError);
        Assert.Equal(5000, McpTestClient.Text(big).Length);
    }

    [Fact]
    public async Task Calls_over_the_minute_limit_are_refused_with_when_to_retry()
    {
        var clock = new ManualClock();
        await using var server = await StartAsync(
            o => o.Limits = new McpCallLimits { CallsPerMinute = 2, WritesPerMinute = 1, CallsPerDay = 100 },
            s => s.AddSingleton<TimeProvider>(clock));

        await server.CallAsync("echo_shade", new { shade = "red" });
        await server.CallAsync("echo_shade", new { shade = "red" });
        var third = await server.CallAsync("echo_shade", new { shade = "red" });
        clock.Advance(TimeSpan.FromSeconds(61));
        var later = await server.CallAsync("echo_shade", new { shade = "red" });

        Assert.True(third.IsError);
        Assert.Equal("Too many requests from this connection. Try again in 60 seconds.", McpTestClient.Text(third));
        Assert.NotEqual(true, later.IsError);
    }

    [Fact]
    public async Task Writes_have_their_own_limit_and_a_tool_without_annotations_counts_as_a_write()
    {
        await using var server = await StartAsync(
            o => o.Limits = new McpCallLimits { CallsPerMinute = 10, WritesPerMinute = 1, CallsPerDay = 100 },
            s => s.AddSingleton<TimeProvider>(new ManualClock()));

        var write = await server.CallAsync("create_note", new { title = "a" });
        var read = await server.CallAsync("echo_shade", new { shade = "red" });
        var unannotated = await server.CallAsync("unannotated");

        Assert.NotEqual(true, write.IsError);
        Assert.NotEqual(true, read.IsError);
        Assert.True(unannotated.IsError);
    }

    [Fact]
    public async Task Callers_are_counted_apart()
    {
        await using var server = await StartAsync(
            o =>
            {
                o.Limits = new McpCallLimits { CallsPerMinute = 1, WritesPerMinute = 1, CallsPerDay = 100 };
                o.CallerKey = ctx => ctx.Params?.Name;
            },
            s => s.AddSingleton<TimeProvider>(new ManualClock()));

        var first = await server.CallAsync("echo_shade", new { shade = "red" });
        var other = await server.CallAsync("ranged", new { limit = 3 });
        var again = await server.CallAsync("echo_shade", new { shade = "red" });

        Assert.NotEqual(true, first.IsError);
        Assert.NotEqual(true, other.IsError);
        Assert.True(again.IsError);
    }

    [Fact]
    public async Task An_observer_sees_every_call_with_its_outcome()
    {
        var observer = new CollectingObserver();
        await using var server = await StartAsync(services: s => s.AddSingleton<IMcpCallObserver>(observer));

        await server.CallAsync("echo_shade", new { shade = "red" });
        await server.CallAsync("refuse");
        await server.CallAsync("crash");
        await server.CallAsync("ranged", new { limit = 500 });
        await server.CallAsync("create_note", new { title = "a" });

        var calls = observer.Calls;
        Assert.Equal(
            new[] { ("echo_shade", McpCallOutcome.Ok), ("refuse", McpCallOutcome.Refused), ("crash", McpCallOutcome.Failed), ("ranged", McpCallOutcome.Refused), ("create_note", McpCallOutcome.Ok) },
            calls.Select(c => (c.Tool, c.Outcome)).ToArray());
        Assert.Equal(new[] { false, false, false, false, true }, calls.Select(c => c.IsWrite).ToArray());
        Assert.All(calls, c => Assert.Equal("local", c.Caller));
        Assert.Equal("Red".Length, calls[0].ResultChars);
        Assert.Null(calls[0].Error);
        Assert.IsType<McpException>(calls[1].Error);
        Assert.IsType<NullReferenceException>(calls[2].Error);
        Assert.Null(calls[3].Error);
    }

    [Fact]
    public async Task An_observer_that_throws_does_not_fail_the_call()
    {
        await using var server = await StartAsync(services: s => s.AddSingleton<IMcpCallObserver, ThrowingObserver>());

        var result = await server.CallAsync("echo_shade", new { shade = "red" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Red", McpTestClient.Text(result));
    }

    [Fact]
    public async Task Calls_are_counted_on_the_studio_meter()
    {
        var seen = new List<(long Value, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == StudioMcpMetrics.MeterName && instrument.Name == "techteastudio.mcp.tool.calls") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? tool = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == StudioMcpMetrics.ToolTag) tool = tag.Value as string;
                if (tag.Key == StudioMcpMetrics.OutcomeTag) outcome = tag.Value as string;
            }

            if (tool == "metered_ping") lock (seen) seen.Add((value, outcome));
        });
        listener.Start();

        await using var server = await McpTestServer.StartAsync(s => s
            .AddStudioMcp(o =>
            {
                o.Name = "metered";
                o.Title = "Metered";
                o.Instructions = Instructions;
            })
            .WithStudioTools<MeteredTools>());
        await server.CallAsync("metered_ping");

        lock (seen) Assert.Equal(new[] { (1L, (string?)"ok") }, seen.ToArray());
    }

    [Fact]
    public async Task An_argument_the_tool_does_not_have_is_refused_with_the_ones_it_has()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("ranged", new { limit = 3, limt = 4 });

        Assert.True(result.IsError);
        Assert.Equal("'limt' is not an argument of this tool; it takes limit.", McpTestClient.Text(result));
        Assert.Equal(0, Runs(server));
    }

    [Fact]
    public async Task Unknown_arguments_can_be_let_through()
    {
        await using var server = await StartAsync(o => o.RejectUnknownArguments = false);

        var result = await server.CallAsync("ranged", new { limit = 3, limt = 4 });

        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task A_null_for_an_optional_argument_takes_its_default()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("paged", new Dictionary<string, object?> { ["page"] = null });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("1", McpTestClient.Text(result));
    }

    [Fact]
    public async Task A_null_for_a_required_argument_is_one_sentence()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("ranged", new Dictionary<string, object?> { ["limit"] = null });

        Assert.True(result.IsError);
        Assert.Equal("limit is required.", McpTestClient.Text(result));
    }

    [Fact]
    public async Task A_writing_tool_is_never_refused_over_budget_because_its_change_has_happened()
    {
        await using var server = await StartAsync(o =>
        {
            o.MaxResultChars = 1000;
            o.OverBudget = OverBudgetBehavior.Refuse;
        });

        var result = await server.CallAsync("write_big", new { chars = 5000 });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(5000, McpTestClient.Text(result).Length);
    }

    [Fact]
    public void An_over_budget_message_that_is_not_a_format_string_fails_at_startup()
    {
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddStudioMcp(o =>
        {
            o.Name = "sample";
            o.Title = "Sample";
            o.Instructions = Instructions;
            o.OverBudgetMessage = "Too big: {0} of {1} {";
        }));

        Assert.Contains("OverBudgetMessage", error.Message);
    }

    [Fact]
    public async Task An_observer_that_times_out_on_its_own_does_not_fail_the_call()
    {
        await using var server = await StartAsync(services: s => s.AddSingleton<IMcpCallObserver, TimingOutObserver>());

        var result = await server.CallAsync("echo_shade", new { shade = "red" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Red", McpTestClient.Text(result));
    }

    [Fact]
    public async Task An_unknown_tool_stays_a_protocol_error()
    {
        await using var server = await StartAsync();

        await Assert.ThrowsAsync<McpProtocolException>(() => server.CallAsync("no_such_tool"));
    }
}

public class LimiterTests
{
    [Fact]
    public async Task The_minute_window_resets_and_the_day_window_holds()
    {
        var clock = new ManualClock();
        var limiter = new InMemoryMcpCallLimiter(clock);
        var limits = new McpCallLimits { CallsPerMinute = 1, WritesPerMinute = 1, CallsPerDay = 2 };

        Assert.True((await Read(limiter, limits)).Allowed);
        var second = await Read(limiter, limits);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await Read(limiter, limits)).Allowed);
        clock.Advance(TimeSpan.FromMinutes(1));
        var dayOver = await Read(limiter, limits);

        Assert.False(second.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(60), second.RetryAfter);
        Assert.False(dayOver.Allowed);
        Assert.Equal(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(2), dayOver.RetryAfter);
    }

    [Fact]
    public async Task A_refused_call_is_not_counted()
    {
        var limiter = new InMemoryMcpCallLimiter(new ManualClock());
        var limits = new McpCallLimits { CallsPerMinute = 5, WritesPerMinute = 1, CallsPerDay = 100 };

        Assert.True((await Write(limiter, limits)).Allowed);
        Assert.False((await Write(limiter, limits)).Allowed);
        Assert.False((await Write(limiter, limits)).Allowed);
        for (var i = 0; i < 4; i++) Assert.True((await Read(limiter, limits)).Allowed);
        Assert.False((await Read(limiter, limits)).Allowed);
    }

    private static ValueTask<McpLimitDecision> Read(IMcpCallLimiter limiter, McpCallLimits limits)
        => limiter.TryAcquireAsync(new McpLimitRequest { Caller = "a", Tool = "read", IsWrite = false, Limits = limits }, default);

    private static ValueTask<McpLimitDecision> Write(IMcpCallLimiter limiter, McpCallLimits limits)
        => limiter.TryAcquireAsync(new McpLimitRequest { Caller = "a", Tool = "write", IsWrite = true, Limits = limits }, default);

    [Theory]
    [InlineData(0.2, "1 second")]
    [InlineData(60, "60 seconds")]
    [InlineData(119, "119 seconds")]
    [InlineData(300, "5 minutes")]
    [InlineData(86_280, "24 hours")]
    public void A_wait_is_said_in_units_a_person_reads(double seconds, string said)
        => Assert.Equal(said, StudioMcpPipeline.Wait(TimeSpan.FromSeconds(seconds)));
}

public class JsonTests
{
    private sealed record Sample(string DisplayName, string? Note, Shade Shade);

    [Fact]
    public void Snake_case_names_enums_keeps_text_and_drops_nulls()
    {
        var json = JsonSerializer.Serialize(new Sample("Привет <b>", null, Shade.DarkBlue), StudioMcpJson.SnakeCase);

        Assert.Equal("{\"display_name\":\"Привет <b>\",\"shade\":\"dark_blue\"}", json);
    }

    [Fact]
    public void Camel_case_names_enums_the_same_way()
    {
        var json = JsonSerializer.Serialize(new Sample("Сеть", null, Shade.DarkBlue), StudioMcpJson.CamelCase);

        Assert.Equal("{\"displayName\":\"Сеть\",\"shade\":\"darkBlue\"}", json);
    }

    [Fact]
    public void The_shared_options_cannot_be_changed()
    {
        Assert.True(StudioMcpJson.SnakeCase.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => StudioMcpJson.SnakeCase.WriteIndented = true);
    }
}
