using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TechTeaStudio.Mcp.Testing;

/// <summary>
/// An MCP server and a client connected to it in memory, through a pair of pipes. Register the server the
/// way the product does (<c>AddMcpServer()</c> or <c>AddStudioMcp(...)</c> and its tools) in the callback;
/// leave the transport out, the test server brings its own. Dispose it at the end of the test.
/// </summary>
public sealed class McpTestServer : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly Task _run;
    private readonly CancellationTokenSource _stop;

    private McpTestServer(ServiceProvider services, McpServer server, Task run, CancellationTokenSource stop, McpClient client)
    {
        _services = services;
        _server = server;
        _run = run;
        _stop = stop;
        Client = client;
    }

    /// <summary>The connected client: list tools, call them, read the server's identity and instructions.</summary>
    public McpClient Client { get; }

    /// <summary>The server's service provider, for a test that needs a registered fake back.</summary>
    public IServiceProvider Services => _services;

    /// <summary>Builds the server from <paramref name="configure"/>, starts it and connects a client.</summary>
    public static async Task<McpTestServer> StartAsync(Action<IServiceCollection> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var services = new ServiceCollection();
        configure(services);
        // After the callback, so a test that registers its own logging keeps it.
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));

        var provider = services.BuildServiceProvider();
        McpServer? server = null;
        CancellationTokenSource? stop = null;
        Task? run = null;
        try
        {
            var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
            var loggers = provider.GetRequiredService<ILoggerFactory>();

            var toServer = new Pipe();
            var toClient = new Pipe();
            server = McpServer.Create(
                new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "mcp-test-server", loggers),
                options, loggers, provider);

            stop = new CancellationTokenSource();
            run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), loggers),
                loggerFactory: loggers, cancellationToken: cancellationToken).ConfigureAwait(false);

            return new McpTestServer(provider, server, run, stop, client);
        }
        catch
        {
            // Nothing half-started outlives a failed start: the server loop, the server and the container.
            stop?.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The start already failed; that exception is the one to report.
                }
            }

            if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
            stop?.Dispose();
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Calls <paramref name="tool"/> with an anonymous object or a dictionary as its arguments.</summary>
    public Task<CallToolResult> CallAsync(string tool, object? arguments = null, CancellationToken cancellationToken = default)
        => McpTestClient.CallAsync(Client, tool, arguments, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        _stop.Cancel();
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _server.DisposeAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>Client helpers for a server reached over HTTP (a test host, a staging URL) or already connected.</summary>
public static class McpTestClient
{
    /// <summary>
    /// Connects to the Streamable HTTP endpoint at <paramref name="endpoint"/> through <paramref name="httpClient"/>
    /// (for ASP.NET Core tests, the client of a <c>WebApplicationFactory</c>). <paramref name="headers"/> carry
    /// e.g. <c>Authorization</c>. The HTTP client stays the caller's.
    /// </summary>
    public static Task<McpClient> ConnectAsync(
        HttpClient httpClient, Uri endpoint, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);

        var options = new HttpClientTransportOptions { Endpoint = endpoint };
        if (headers is not null) options.AdditionalHeaders = new Dictionary<string, string>(headers);
        var transport = new HttpClientTransport(options, httpClient, NullLoggerFactory.Instance, false);
        return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    /// <summary>Calls <paramref name="tool"/> with an anonymous object or a dictionary as its arguments.</summary>
    public static async Task<CallToolResult> CallAsync(McpClient client, string tool, object? arguments = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await client.CallToolAsync(tool, Arguments(arguments), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The text of an answer's text blocks, joined by a newline.</summary>
    public static string Text(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    /// <summary>Turns an anonymous object, a dictionary or null into tool arguments.</summary>
    public static IReadOnlyDictionary<string, object?> Arguments(object? arguments)
    {
        if (arguments is null) return new Dictionary<string, object?>();
        if (arguments is IReadOnlyDictionary<string, object?> ready) return ready;
        if (arguments is IDictionary<string, object?> dictionary) return new Dictionary<string, object?>(dictionary);

        var element = JsonSerializer.SerializeToElement(arguments);
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool arguments must be an object: an anonymous object or a dictionary.", nameof(arguments));
        return element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }
}
