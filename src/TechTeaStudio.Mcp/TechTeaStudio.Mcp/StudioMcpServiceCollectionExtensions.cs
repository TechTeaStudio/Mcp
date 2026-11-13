using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TechTeaStudio.Mcp;

/// <summary>Registers an MCP server with the studio conventions.</summary>
public static class StudioMcpServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MCP server (<c>AddMcpServer</c>) with its identity and instructions from
    /// <paramref name="configure"/>, and the call pipeline: argument checks, refusals, limits, the answer
    /// budget, observers and metrics. Returns the SDK's builder, so the transport and the tools are added the
    /// usual way; <see cref="WithStudioTools{TTools}"/> registers tools with <see cref="StudioMcpOptions.Json"/>.
    /// Throws <see cref="ArgumentException"/> when the name, title or instructions are missing, so a server
    /// that would not say who it is fails at startup rather than in front of a model.
    /// </summary>
    public static IMcpServerBuilder AddStudioMcp(this IServiceCollection services, Action<StudioMcpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new StudioMcpOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        // IMcpCallLimiter -> McpCallLimiter.cs (InMemoryMcpCallLimiter) unless the host registered its own first
        services.TryAddSingleton<IMcpCallLimiter, InMemoryMcpCallLimiter>();

        // Once, when the server's options are built: studio JSON leaves nulls out, while the SDK's output
        // schema lists every property as required, so a client that checks structured content against the
        // schema would reject an answer whose deadline is null. Done here rather than on every tools/list, so
        // the shared tool objects are changed once and never under concurrent requests.
        services.PostConfigure<McpServerOptions>(server =>
        {
            if (options.Json.DefaultIgnoreCondition is not (JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault)) return;
            if (server.ToolCollection is not { } tools) return;
            foreach (var tool in tools)
                if (tool.ProtocolTool.OutputSchema is { } schema)
                    tool.ProtocolTool.OutputSchema = McpSchemaAlignment.DropNullableFromRequired(schema);
        });

        return services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = options.Name!.Trim(),
                    Title = options.Title!.Trim(),
                    Version = string.IsNullOrWhiteSpace(options.Version) ? DefaultVersion() : options.Version!,
                    WebsiteUrl = options.WebsiteUrl,
                };
                server.ServerInstructions = options.Instructions;
            })
            .WithRequestFilters(filters => filters.AddCallToolFilter(StudioMcpPipeline.CallTool(options)));
    }

    /// <summary>Registers the <c>[McpServerTool]</c> methods of <typeparamref name="TTools"/> with <see cref="StudioMcpOptions.Json"/>.</summary>
    public static IMcpServerBuilder WithStudioTools<TTools>(this IMcpServerBuilder builder)
        where TTools : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithTools<TTools>(OptionsOf(builder).Json);
    }

    /// <summary>Registers the <c>[McpServerPrompt]</c> methods of <typeparamref name="TPrompts"/> with <see cref="StudioMcpOptions.Json"/>.</summary>
    public static IMcpServerBuilder WithStudioPrompts<TPrompts>(this IMcpServerBuilder builder)
        where TPrompts : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithPrompts<TPrompts>(OptionsOf(builder).Json);
    }

    private static StudioMcpOptions OptionsOf(IMcpServerBuilder builder)
    {
        foreach (var descriptor in builder.Services)
        {
            if (descriptor.ServiceType == typeof(StudioMcpOptions)
                && !descriptor.IsKeyedService
                && descriptor.ImplementationInstance is StudioMcpOptions options)
                return options;
        }

        throw new InvalidOperationException("Call AddStudioMcp before WithStudioTools or WithStudioPrompts: they register with its JSON options.");
    }

    private static string DefaultVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational!.IndexOf('+');
            return plus > 0 ? informational.Substring(0, plus) : informational;
        }

        return assembly?.GetName().Version?.ToString() ?? "0.0.0";
    }
}
