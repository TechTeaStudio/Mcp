using System.Text.Json;
using System.Text.Json.Nodes;

namespace TechTeaStudio.Mcp;

/// <summary>
/// Makes an output schema agree with JSON that leaves nulls out. The SDK lists every property of a
/// structured answer as required, nullable ones included; with nulls omitted, an answer whose deadline is
/// null has no "deadline" at all, and a client that checks structured content against the schema rejects
/// it. A property that may be null is therefore taken off "required", at every depth.
/// </summary>
internal static class McpSchemaAlignment
{
    internal static JsonElement DropNullableFromRequired(JsonElement schema)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not { } node) return schema;
        if (!Align(node)) return schema;

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <returns>True when anything changed.</returns>
    private static bool Align(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject properties && obj["required"] is JsonArray required)
                {
                    for (var i = required.Count - 1; i >= 0; i--)
                    {
                        if (required[i] is JsonValue entry
                            && entry.TryGetValue<string>(out var name)
                            && properties[name] is JsonObject property
                            && AllowsNull(property))
                        {
                            required.RemoveAt(i);
                            changed = true;
                        }
                    }
                }

                foreach (var child in obj.Select(pair => pair.Value).ToList())
                    if (child is not null) changed |= Align(child);
                break;

            case JsonArray array:
                foreach (var child in array.ToList())
                    if (child is not null) changed |= Align(child);
                break;
        }

        return changed;
    }

    private static bool AllowsNull(JsonObject schema)
    {
        switch (schema["type"])
        {
            case JsonValue single when single.TryGetValue<string>(out var type):
                return type == "null";
            case JsonArray types:
                return types.OfType<JsonValue>().Any(t => t.TryGetValue<string>(out var name) && name == "null");
        }

        return (schema["anyOf"] ?? schema["oneOf"]) is JsonArray options
               && options.OfType<JsonObject>().Any(AllowsNull);
    }
}
