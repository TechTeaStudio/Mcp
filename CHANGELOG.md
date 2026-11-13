# Changelog

All notable changes to these packages are documented here.
Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-11-13

First release: the conventions every Tech Tea Studio MCP server follows, and the contract test that checks them. Both packages target `net6.0` through `net11.0` and build on `ModelContextProtocol` 2.4.0.

### Added: `TechTeaStudio.Mcp`

- **`AddStudioMcp(options)`**, which registers the MCP server and returns the SDK's builder. `Name`, `Title` and `Instructions` are required and checked at registration; `Version` defaults to the entry assembly's informational version, `WebsiteUrl` is optional.
- **`WithStudioTools<T>()`** and **`WithStudioPrompts<T>()`**, which register with the configured JSON.
- **`StudioMcpJson.SnakeCase`**, **`CamelCase`** and **`Create(policy)`**: the SDK's options with the naming policy, a relaxed encoder (non-Latin text stays readable), string enums in the same naming placed ahead of the SDK's PascalCase converter, and nulls left out.
- **The call pipeline**, a request filter around every tool:
  - per-caller limits (`McpCallLimits`, `IMcpCallLimiter.TryAcquireAsync` with an `McpLimitRequest`, `InMemoryMcpCallLimiter`), writes told apart by the `readOnlyHint` annotation, the wait said in seconds, minutes or hours;
  - argument checks against the tool's input schema with one sentence per problem, unknown arguments refused (`RejectUnknownArguments`), a `null` for an optional parameter taken out so its default applies;
  - `McpException` and `IsRefusal` exceptions answered with their message alone, a `JsonException` answered as unreadable arguments and logged, `McpProtocolException` passed through, anything else answered with `FailureMessage` and logged;
  - an answer budget (`MaxResultChars`, `OverBudget`, `OverBudgetMessage`, checked at startup) that refuses only a read-only tool's answer;
  - `IMcpCallObserver` with `McpCallRecord` (a class with init-only properties, the ending exception included), resolved from the request scope; an observer's own failure or timeout never fails the call;
  - the `TechTeaStudio.Mcp` meter: `techteastudio.mcp.tool.calls` and `techteastudio.mcp.tool.duration` (seconds), tagged `gen_ai.tool.name` and `techteastudio.mcp.outcome`.
- **Output schema alignment**, once when the server's options are built: properties that may be null leave `required`, so structured answers without them validate.

### Added: `TechTeaStudio.Mcp.Testing`

- **`McpContract.CheckAsync(client, options)`** with 26 rules, `TTS-MCP-001` to `TTS-MCP-026`, listed in the README: the server, its tools, prompts and instructions, and the answers to probes.
- **`McpContractOptions`**: `ProductName`, thresholds, `FailureMessages`, `Probe`, `ProbeError` (with an optional `containing` text), `Allow` and `Severity`.
- **`McpContractReport`** with `Errors`, `Warnings`, `IsClean` and `EnsureNoErrors`, which throws **`McpContractException`** listing every finding, so any test framework works.
- **`McpTestServer.StartAsync(configure)`**, an in-memory server and client, and **`McpTestClient`** with `ConnectAsync` for a remote endpoint, `CallAsync`, `Text` and `Arguments`.
- 58 tests on `net8.0`, `net9.0`, `net10.0` and `net11.0`. On `net6.0` and `net7.0` the SDK's netstandard2.0 build does not put `[Range]` and similar bounds into the schema, so the argument check cannot hold a call to them there; checked by hand on the .NET 6 runtime.
