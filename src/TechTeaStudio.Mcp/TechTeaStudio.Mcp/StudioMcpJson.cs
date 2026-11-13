using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace TechTeaStudio.Mcp;

/// <summary>
/// The JSON a studio server answers in. Built on the SDK's own options and changed in four ways:
/// <list type="bullet">
/// <item>the property naming policy is the caller's (snake_case or camelCase);</item>
/// <item>non-Latin text stays as it is, where the SDK default writes Cyrillic as <c>\u041F</c> escapes that cost
/// six times the tokens and that a model has to decode;</item>
/// <item>enums are strings in the same naming, first in the converter list so they win over the SDK's own
/// PascalCase converter: the schema then lists <c>dark_blue</c>, and <c>dark_blue</c> binds;</item>
/// <item>nulls are left out.</item>
/// </list>
/// Pass the same options to any <c>JsonSerializer</c> call whose output a tool returns as text.
/// </summary>
public static class StudioMcpJson
{
    /// <summary>snake_case properties and enum values. The studio default for tools.</summary>
    public static JsonSerializerOptions SnakeCase { get; } = Create(JsonNamingPolicy.SnakeCaseLower);

    /// <summary>camelCase properties and enum values.</summary>
    public static JsonSerializerOptions CamelCase { get; } = Create(JsonNamingPolicy.CamelCase);

    /// <summary>Read-only options with the studio conventions and <paramref name="namingPolicy"/>.</summary>
    public static JsonSerializerOptions Create(JsonNamingPolicy namingPolicy)
    {
        ArgumentNullException.ThrowIfNull(namingPolicy);

        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            PropertyNamingPolicy = namingPolicy,
            // Safe for an MCP answer: it is read by a model, never written into an HTML page. The HTML-
            // sensitive characters the default encoder escapes (< > & ' + `) are plain text here.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Insert(0, new JsonStringEnumConverter(namingPolicy, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
