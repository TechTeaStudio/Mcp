<p align="center">
  <img src="https://raw.githubusercontent.com/TechTeaStudio/Mcp/product/src/TechTeaStudio.Mcp/icon.png" alt="TechTeaStudio.Mcp logo" width="160" />
</p>

<h1 align="center">TechTeaStudio.Mcp</h1>

<p align="center">
  Studio conventions for Model Context Protocol servers on the official C# SDK, and a contract test that catches what makes a model guess.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TechTeaStudio.Mcp"><img alt="NuGet" src="https://img.shields.io/nuget/v/TechTeaStudio.Mcp.svg?logo=nuget&label=NuGet" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Mcp"><img alt="Downloads" src="https://img.shields.io/nuget/dt/TechTeaStudio.Mcp.svg?logo=nuget&label=Downloads" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Mcp.Testing"><img alt="Testing" src="https://img.shields.io/nuget/v/TechTeaStudio.Mcp.Testing.svg?logo=nuget&label=Testing" /></a>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-6.0%20%E2%86%92%2011.0-512BD4?logo=dotnet&logoColor=white" />
  <a href="https://github.com/TechTeaStudio/Mcp/actions/workflows/dotnet.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/TechTeaStudio/Mcp/dotnet.yml?branch=product&logo=github&label=build" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
</p>

## What this gives you

The official `ModelContextProtocol` SDK speaks the protocol. What it leaves to every server is the part a model actually meets: who the server is, how its answers read, what an error says. This repository standardises that part for every Tech Tea Studio server, local (stdio) or remote (Streamable HTTP).

**`TechTeaStudio.Mcp`**, one `AddStudioMcp` call:

- **Identity that cannot be forgotten.** Name, title and instructions are required at startup. A server that does not say who it is leaves the model guessing which connected server a request is for.
- **JSON a model reads cheaply.** Non-Latin text stays as it is (the SDK default writes `Привет` as `\u041F\u0440\u0438\u0432\u0435\u0442`), enums are strings in the schema in the same case as the properties, nulls are left out, and output schemas stop requiring the fields that are left out.
- **Arguments checked against the tool's own schema** before the tool runs: required values, types, allowed values, bounds, lengths, item counts, into arrays and nested objects. The model reads `limit: 500 is above the maximum of 50.` instead of a silently clamped value, `shade: 'purple' is not one of red, dark_blue.` instead of the SDK's `An error occurred invoking 'tool'.`, and `'limt' is not an argument of this tool; it takes limit.` instead of a misspelt argument the binder drops without a word. A `null` sent for an optional parameter lets its default apply.
- **Refusals as written.** Throw `McpException`, or any exception your `IsRefusal` accepts (a domain exception), and the model gets the sentence alone. An unexpected exception answers a generic sentence and goes to the log, details included.
- **Per-caller rate limits**, counted per tool call, with writes held to a stricter limit than reads. Which is which comes from the tool's `readOnlyHint`, so there is no second flag to keep in step.
- **An answer budget.** Warn about, or refuse, a read-only tool's answers over 40 000 characters (about 10 000 tokens; Claude Code cuts tool answers at 25 000). A writing tool's answer is only logged: its change has happened, and a refusal would make the model write again.
- **`IMcpCallObserver`** for activity journals and audits, and the `TechTeaStudio.Mcp` meter for OpenTelemetry (`techteastudio.mcp.tool.calls` and `techteastudio.mcp.tool.duration`, beside the SDK's own `mcp.*` metrics).

**`TechTeaStudio.Mcp.Testing`**, for the test project:

- **`McpContract.CheckAsync`** connects as a client and reports what a model would trip over, under 26 rules: no title or instructions, the product never named, tools and prompts without descriptions or annotations, allowed values and bounds written in prose instead of the schema, long dashes, refusals sent as data, crashes passed off as refusals, the SDK's bare error text, escaped text, answers over budget, structured content that breaks its own schema, a tool list too big for the context, instructions that name a tool the server no longer has. It judges a bare-SDK server exactly like a studio one, so it is also the way to find out what adopting the conventions would fix.
- **`McpTestServer`** runs any server in memory; **`McpTestClient`** connects to a remote one (an ASP.NET Core test host, a staging URL).

## How it compares

| | Bare SDK | With `AddStudioMcp` |
|---|---|---|
| Name, title, instructions | optional; the name defaults to the assembly | required at startup |
| Cyrillic in a text answer | `\u041F\u0440\u0438\u0432\u0435\u0442` | `Привет` |
| Enum parameter | PascalCase in the schema; `dark_blue` fails to bind | `dark_blue` in the schema and in binding |
| Unknown enum value | `An error occurred invoking 'tool'.` | `shade: 'purple' is not one of red, dark_blue.` |
| `[Range(1, 50)]` | in the schema, not enforced | enforced, with the bound in the sentence |
| `throw new McpException("Use a date.")` | `An error occurred invoking 'tool': Use a date.` | `Use a date.` |
| Unexpected exception | `An error occurred invoking 'tool'.` | your `FailureMessage`, exception logged |
| Nullable field of a structured answer | required by the schema, missing from the answer | not required |
| Rate limits, answer budget, observers, metrics | none | built in |

Tools stay plain `[McpServerTool]` methods. Nothing here wraps the SDK's attributes or its transports, so a new SDK version reaches you without waiting for this package.

## Install

```bash
dotnet add package TechTeaStudio.Mcp
```

In the test project:

```bash
dotnet add package TechTeaStudio.Mcp.Testing
```

## Packages

| Package | What it holds | Frameworks | References |
|---|---|---|---|
| `TechTeaStudio.Mcp` | `AddStudioMcp`, `StudioMcpJson`, the call pipeline, `IMcpCallLimiter`, `IMcpCallObserver`, `StudioMcpMetrics` | net6.0 → net11.0 | `ModelContextProtocol` 2.4.0+ |
| `TechTeaStudio.Mcp.Testing` | `McpContract`, `McpTestServer`, `McpTestClient` | net6.0 → net11.0 | `ModelContextProtocol` 2.4.0+, `Microsoft.Extensions.DependencyInjection` |

The testing package does not reference the conventions package: the contract is checked from the client side, so it works on any server.

On net6.0 and net7.0 both packages run on the SDK's netstandard2.0 build, and one thing differs there: that build does not turn `[Range]`, `[StringLength]` and the like into `minimum`, `maximum` or `maxLength` in the schema, so the argument check has no bounds to hold a call to. Enums, required values, types, unknown arguments and everything else work as on net8.0 and later; the tests run on net8.0 to net11.0, and the net6.0 build was checked by hand on the .NET 6 runtime.

## Quick start

### A local server over stdio

```csharp
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();          // stdout carries the protocol only

builder.Services
    .AddStudioMcp(o =>
    {
        o.Name = "trafficmeter";
        o.Title = "TrafficMeter";
        o.Instructions = "TrafficMeter counts this computer's internet traffic ... Start with get_status.";
        o.Json = StudioMcpJson.CamelCase;   // snake_case is the default
        o.OverBudget = OverBudgetBehavior.Refuse;
    })
    .WithStdioServerTransport()
    .WithStudioTools<TrafficTools>();

builder.Build().Run();
```

### A remote server

```csharp
builder.Services
    .AddStudioMcp(o =>
    {
        o.Name = "chronos";
        o.Title = "Chronos";
        o.WebsiteUrl = "https://chronos.techteastudio.cc";
        o.Instructions = ChronosMcpInstructions.Text;
        o.Limits = new McpCallLimits { CallsPerMinute = 30, WritesPerMinute = 10, CallsPerDay = 1000 };
        o.CallerKey = ctx => ctx.User?.FindFirst("chronos:mcp_token")?.Value;   // one budget per connection
        o.IsRefusal = ex => ex is DomainException;
    })
    .WithHttpTransport(t => t.SessionMode = HttpServerSessionMode.Stateless)   // ModelContextProtocol.AspNetCore
    .WithStudioTools<ChronosMcpTools>()
    .WithStudioPrompts<ChronosMcpPrompts>();
```

### Tools

```csharp
public enum Horizon { Day, Week, Month, Quarter, All }

[McpServerToolType]
public sealed class TaskTools
{
    [McpServerTool(Name = "find_tasks", Title = "Find tasks", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the person's Chronos tasks for a horizon, hottest first.")]
    public TaskList FindTasks(
        [Description("Which tasks to list. Week when omitted.")] Horizon horizon = Horizon.Week,
        [Description("How many rows to return.")][Range(1, 50)] int limit = 25)
        => horizon == Horizon.All && limit > 40
            ? throw new McpException("Ask for at most 40 rows when the horizon is all.")
            : ...;
}
```

The schema the model reads says `"enum": ["day", "week", "month", "quarter", "all"]` and `"minimum": 1, "maximum": 50`, and the pipeline holds the call to both.

## The call pipeline

Every tool call goes through the same steps, in this order:

1. **Limits.** With `Limits` set, the caller (`CallerKey`, else the `NameIdentifier` or `sub` claim, else `local`) is counted per minute and per day; a tool whose `readOnlyHint` is not true counts as a write. Over the limit the model reads `Too many requests from this connection. Try again in 42 seconds.` (or minutes, or hours). `IMcpCallLimiter.TryAcquireAsync` gets the caller, the tool and the limits: swap it for a shared store when more than one process serves the endpoint.
2. **Arguments**, against the tool's input schema (`ValidateArguments`, on by default). A number written as a string passes, as it does in the SDK's own binder; a `null` for an optional parameter is taken out so its default applies; an argument the tool does not have is refused (`RejectUnknownArguments`, on by default).
3. **The tool.** `McpException` and anything `IsRefusal` accepts become an error with the message alone; a `JsonException` is answered as unreadable arguments and logged; `McpProtocolException` passes through untouched, because it is a protocol signal, not a refusal; anything else becomes `FailureMessage` and an error log entry.
4. **The budget.** An answer over `MaxResultChars` is logged, or, from a read-only tool, refused with `OverBudgetMessage` when `OverBudget` is `Refuse`.
5. **Observers and metrics.** Every `IMcpCallObserver` registered in the container sees an `McpCallRecord` (tool, write or read, outcome, caller, duration, answer length, user, and the exception that ended the call), resolved from the request scope so it can share the tool's unit of work. The `TechTeaStudio.Mcp` meter counts `techteastudio.mcp.tool.calls` and times `techteastudio.mcp.tool.duration` in seconds, tagged `gen_ai.tool.name` and `techteastudio.mcp.outcome`.

Once, when the server's options are built, output schemas drop nullable properties from `required`, because the studio JSON leaves nulls out.

## The contract test

```csharp
[Fact]   // or [Test]: the report throws, so any framework works
public async Task The_mcp_contract_holds()
{
    await using var server = await McpTestServer.StartAsync(s => TrafficMeterMcp.Register(s));

    var report = await McpContract.CheckAsync(server.Client, new McpContractOptions { ProductName = "TrafficMeter" }
        .Probe("get_status")
        .Probe("get_report", new { period = "30d" })
        .ProbeError("get_usage", new { period = "next tuesday" }));

    report.EnsureNoErrors();
}
```

For a remote server, connect through the test host instead: `await McpTestClient.ConnectAsync(factory.CreateClient(), new Uri(factory.Server.BaseAddress, "/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer ..." })`.

`Probe` calls a tool and expects an answer; `ProbeError` expects a refusal a model can act on: not the generic failure sentence (`FailureMessages`) and, with `containing:`, one that says the given words. Every probe answer is also checked for size, escaped text, noisy decimals and its output schema. `Allow("TTS-MCP-014", "get_usage.period")` waives a rule where breaking it is deliberate, for one target or (without a target) everywhere; `Severity("TTS-MCP-016", McpContractSeverity.Warning)` moves a rule to another severity.

| Rule | Severity | What it catches |
|---|---|---|
| TTS-MCP-001 | error | The server sends no title |
| TTS-MCP-002 | error | No instructions, or fewer than 80 characters |
| TTS-MCP-003 | warning | Instructions over 4 000 characters; they ride along in every conversation |
| TTS-MCP-004 | error | The instructions never name the product (`ProductName`) |
| TTS-MCP-005 | warning | No tool title or description names the product |
| TTS-MCP-006 | error | A tool name breaks the MCP format |
| TTS-MCP-007 | warning | A tool name is not snake_case |
| TTS-MCP-008 | error | A tool has no title |
| TTS-MCP-009 | error | `readOnlyHint` not stated, or a writing tool without `destructiveHint` and `idempotentHint` |
| TTS-MCP-010 | warning | `openWorldHint` not stated |
| TTS-MCP-011 | error | A description is missing or under 20 characters |
| TTS-MCP-012 | warning | A description over 1 024 characters |
| TTS-MCP-013 | error | A parameter has no description |
| TTS-MCP-014 | error | Allowed values opening a text parameter's description ("day, week or month", "priority: none, low or high") with no enum in the schema |
| TTS-MCP-015 | warning | Bounds written in prose ("1 to 50") with no minimum or maximum in the schema |
| TTS-MCP-016 | error | A long dash in a title, a description or the instructions (a studio rule; move it with `Severity`) |
| TTS-MCP-017 | error | A refusal sent back as a normal answer, a crash passed off as a refusal, a refusal without the expected words, or an expected answer that failed |
| TTS-MCP-018 | error | The SDK's bare `An error occurred invoking` with nothing to act on; a warning when it only prefixes the real message |
| TTS-MCP-019 | error | An answer over the budget (40 000 characters) |
| TTS-MCP-020 | error | `\u` escapes of non-Latin or HTML-sensitive characters in an answer |
| TTS-MCP-021 | warning | Numbers with five or more decimal places |
| TTS-MCP-022 | error | Structured content missing a property its output schema requires |
| TTS-MCP-023 | error | A probe names a tool the server does not list |
| TTS-MCP-024 | error | A prompt has no description (a warning for a prompt argument without one) |
| TTS-MCP-025 | warning | `tools/list` over 40 000 characters of JSON |
| TTS-MCP-026 | warning | The instructions name a snake_case identifier no tool, prompt, parameter or output field has |

## Options

| `StudioMcpOptions` | Default | Meaning |
|---|---|---|
| `Name`, `Title`, `Instructions` | required | The server's identity and the model's system context |
| `Version` | entry assembly version | Sent with the identity |
| `WebsiteUrl` | none | Sent with the identity |
| `Json` | `StudioMcpJson.SnakeCase` | What `WithStudioTools` and `WithStudioPrompts` register with |
| `Limits` | none | Per-caller calls and writes per minute, calls per day |
| `CallerKey` | `NameIdentifier`, `sub`, `local` | Who a call is counted against |
| `IsRefusal` | none | Exceptions whose message the model should read |
| `FailureMessage` | "Something went wrong on our side. Try again in a moment." | What an unexpected failure answers |
| `ValidateArguments` | true | Check arguments against the input schema |
| `RejectUnknownArguments` | true | Refuse an argument the tool does not have |
| `MaxResultChars`, `OverBudget`, `OverBudgetMessage` | 40 000, warn | The answer budget |

## Versioning

Both packages carry the same version and are released together from the `product` branch. Until 1.0 a minor version may change behaviour; every change is in [CHANGELOG.md](CHANGELOG.md). The SDK dependency is a floor (2.4.0), not a pin.

## License

MIT, see [LICENSE](LICENSE).
