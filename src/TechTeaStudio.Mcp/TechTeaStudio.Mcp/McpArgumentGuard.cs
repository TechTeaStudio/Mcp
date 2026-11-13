using System.Globalization;
using System.Text.Json;

namespace TechTeaStudio.Mcp;

/// <summary>
/// Checks tool arguments against the tool's own input schema before the tool runs: required values, types,
/// allowed values, numeric bounds, lengths and item counts, into arrays and nested objects. The SDK binds
/// arguments but reports a bad enum value as "An error occurred invoking 'tool'." and does not enforce
/// bounds at all; this says which argument, what was wrong and what is allowed.
///
/// <para>Deliberately lenient where the binder is: a number in a string passes for a number (the SDK reads
/// numbers from strings), and a schema it does not understand (<c>anyOf</c>, <c>$ref</c>) is not judged.</para>
/// </summary>
internal static class McpArgumentGuard
{
    private const int MaxProblems = 5;
    private const int MaxEcho = 40;

    /// <summary>Null when the arguments fit the schema; else one sentence per problem, at most five.</summary>
    /// <param name="rejectUnknown">Also refuse a top-level argument the schema does not list, naming the ones it does.</param>
    internal static string? Check(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments, bool rejectUnknown = true)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object) return null;

        var problems = new List<string>();
        if (rejectUnknown && arguments is { Count: > 0 } && inputSchema.TryGetProperty("properties", out var known) && known.ValueKind == JsonValueKind.Object)
        {
            var names = known.EnumerateObject().Select(p => p.Name).ToList();
            var unknown = arguments.Keys.Where(k => !names.Contains(k, StringComparer.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                var listed = names.Count == 0 ? "it takes none" : "it takes " + string.Join(", ", names);
                problems.Add(unknown.Count == 1
                    ? $"'{Echo(unknown[0])}' is not an argument of this tool; {listed}."
                    : $"{string.Join(", ", unknown.Select(u => "'" + Echo(u) + "'"))} are not arguments of this tool; {listed}.");
            }
        }

        CheckProperties(inputSchema, name => arguments is not null && arguments.TryGetValue(name, out var value) ? value : null, "", problems);
        return problems.Count == 0 ? null : string.Join(" ", problems.Take(MaxProblems));
    }

    /// <summary>
    /// Takes out a <c>null</c> sent for an optional parameter whose type cannot be null, so the parameter's own
    /// default applies, which is what a model sending null for "no preference" means. The binder would refuse it.
    /// </summary>
    internal static void DropNullsForDefaults(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is not { Count: > 0 } || inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        var required = inputSchema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in arguments.Where(a => a.Value.ValueKind == JsonValueKind.Null).Select(a => a.Key).ToList())
        {
            if (!required.Contains(name) && properties.TryGetProperty(name, out var schema) && !AllowsNull(schema))
                arguments.Remove(name);
        }
    }

    private static void CheckProperties(JsonElement schema, Func<string, JsonElement?> lookup, string prefix, List<string> problems)
    {
        schema.TryGetProperty("properties", out var properties);
        var hasProperties = properties.ValueKind == JsonValueKind.Object;

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in required.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) continue;
                var name = entry.GetString()!;
                var value = lookup(name);
                var allowsNull = hasProperties && properties.TryGetProperty(name, out var propertySchema) && AllowsNull(propertySchema);
                if (value is null || (value.Value.ValueKind == JsonValueKind.Null && !allowsNull))
                    problems.Add($"{Join(prefix, name)} is required.");
            }
        }

        if (!hasProperties) return;
        foreach (var property in properties.EnumerateObject())
        {
            // A null in a required argument has already been reported as missing.
            if (lookup(property.Name) is { } value && !(value.ValueKind == JsonValueKind.Null && IsRequired(schema, property.Name)))
                CheckValue(property.Value, value, Join(prefix, property.Name), problems);
        }
    }

    private static void CheckValue(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (value.ValueKind == JsonValueKind.Null)
        {
            var types = TypesOf(schema);
            if (types.Count > 0 && !types.Contains("null")) problems.Add($"{path} cannot be null.");
            return;
        }

        var typeProblem = TypeProblem(schema, value, path);
        if (typeProblem is not null)
        {
            problems.Add(typeProblem);
            return;
        }

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var names = allowed.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList();
            if (names.Count > 0 && !names.Any(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{path}: '{Echo(text)}' is not one of {string.Join(", ", names)}.");
            return;
        }

        if (NumberOf(value) is { } number)
        {
            if (Bound(schema, "maximum") is { } max && number > max.Value)
                problems.Add($"{path}: {Raw(value)} is above the maximum of {max.Raw}.");
            else if (Bound(schema, "exclusiveMaximum") is { } emax && number >= emax.Value)
                problems.Add($"{path}: {Raw(value)} must be below {emax.Raw}.");
            else if (Bound(schema, "minimum") is { } min && number < min.Value)
                problems.Add($"{path}: {Raw(value)} is below the minimum of {min.Raw}.");
            else if (Bound(schema, "exclusiveMinimum") is { } emin && number <= emin.Value)
                problems.Add($"{path}: {Raw(value)} must be above {emin.Raw}.");
            if (value.ValueKind == JsonValueKind.Number || !TypesOf(schema).Contains("string")) return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var length = value.GetString()!.Length;
                if (Bound(schema, "maxLength") is { } maxLength && length > maxLength.Value)
                    problems.Add($"{path}: {length} characters, more than the maximum of {maxLength.Raw}.");
                else if (Bound(schema, "minLength") is { } minLength && length < minLength.Value)
                    problems.Add($"{path}: {length} characters, fewer than the minimum of {minLength.Raw}.");
                break;
            }

            case JsonValueKind.Array:
            {
                var count = value.GetArrayLength();
                if (Bound(schema, "maxItems") is { } maxItems && count > maxItems.Value)
                    problems.Add($"{path}: {count} items, more than the maximum of {maxItems.Raw}.");
                else if (Bound(schema, "minItems") is { } minItems && count < minItems.Value)
                    problems.Add($"{path}: {count} items, fewer than the minimum of {minItems.Raw}.");

                if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                {
                    var index = 0;
                    foreach (var element in value.EnumerateArray())
                    {
                        CheckValue(items, element, $"{path}[{index}]", problems);
                        index++;
                        if (problems.Count >= MaxProblems) break;
                    }
                }

                break;
            }

            case JsonValueKind.Object:
            {
                var target = value;
                CheckProperties(schema, name => target.TryGetProperty(name, out var inner) ? inner : null, path, problems);
                break;
            }
        }
    }

    /// <summary>A sentence when the value cannot be the schema's type, else null. No type: anything goes.</summary>
    private static string? TypeProblem(JsonElement schema, JsonElement value, string path)
    {
        var types = TypesOf(schema);
        if (types.Count == 0) return null;
        if (types.Any(t => Fits(t, value))) return null;

        if (types.Contains("integer")) return $"{path} must be an integer.";
        if (types.Contains("number")) return $"{path} must be a number.";
        if (types.Contains("boolean")) return $"{path} must be true or false.";
        if (types.Contains("string")) return $"{path} must be a string.";
        if (types.Contains("array")) return $"{path} must be a list.";
        if (types.Contains("object")) return $"{path} must be an object.";
        return null;
    }

    private static bool Fits(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => NumberOf(value) is { } n && Math.Abs(n % 1) < double.Epsilon,
        "number" => NumberOf(value) is not null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    /// <summary>The value as a number: a JSON number, or a string that reads as one (the SDK accepts those).</summary>
    private static double? NumberOf(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static HashSet<string> TypesOf(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("type", out var type)) return types;
        if (type.ValueKind == JsonValueKind.String)
            types.Add(type.GetString()!);
        else if (type.ValueKind == JsonValueKind.Array)
            foreach (var entry in type.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String) types.Add(entry.GetString()!);
        return types;
    }

    private static bool AllowsNull(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return true;
        var types = TypesOf(schema);
        if (types.Contains("null")) return true;
        if (types.Count > 0) return false;
        // No "type": nothing to refuse a null with, unless the allowed values say otherwise.
        return !schema.TryGetProperty("enum", out var allowed)
               || allowed.ValueKind != JsonValueKind.Array
               || allowed.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Null);
    }

    private static bool IsRequired(JsonElement schema, string name)
        => schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
           && required.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == name);

    private static (double Value, string Raw)? Bound(JsonElement schema, string keyword)
        => schema.TryGetProperty(keyword, out var bound) && bound.ValueKind == JsonValueKind.Number && bound.TryGetDouble(out var value)
            ? (value, bound.GetRawText())
            : null;

    private static string Raw(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? Echo(value.GetString()!) : value.GetRawText();

    private static string Echo(string text) => text.Length > MaxEcho ? text.Substring(0, MaxEcho) + "..." : text;

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
}
