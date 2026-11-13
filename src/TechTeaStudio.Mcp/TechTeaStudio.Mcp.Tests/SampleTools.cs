using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace TechTeaStudio.Mcp.Tests;

public enum Shade
{
    Red,
    DarkBlue,
}

public sealed record Row(string Title, string? Missing, Shade Shade);

public sealed record NoteInput(
    [property: Description("The note's title.")] string Title,
    [property: Description("The note's shade.")] Shade? Shade = null);

/// <summary>Counts how often a tool body actually ran, so a test can prove the pipeline stopped a call first.</summary>
public sealed class RunCounter
{
    private int _runs;

    public int Runs => Volatile.Read(ref _runs);

    public void Hit() => Interlocked.Increment(ref _runs);
}

[McpServerToolType]
public sealed class SampleTools
{
    private readonly RunCounter _counter;

    public SampleTools(RunCounter counter) => _counter = counter;

    [McpServerTool(Name = "echo_shade", Title = "Echo shade", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the Sample shade it was given.")]
    public string EchoShade([Description("The shade to echo.")] Shade shade)
    {
        _counter.Hit();
        return shade.ToString();
    }

    [McpServerTool(Name = "ranged", Title = "Ranged", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the Sample limit it was given.")]
    public int Ranged([Description("How many rows to return.")][Range(1, 50)] int limit)
    {
        _counter.Hit();
        return limit;
    }

    [McpServerTool(Name = "create_note", Title = "Create note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a Sample note and returns its title.")]
    public string CreateNote([Description("The note's title.")][StringLength(20)] string title)
    {
        _counter.Hit();
        return title;
    }

    [McpServerTool(Name = "create_notes", Title = "Create notes", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates several Sample notes and returns how many.")]
    public int CreateNotes([Description("The notes to create.")] IReadOnlyList<NoteInput> notes)
    {
        _counter.Hit();
        return notes.Count;
    }

    [McpServerTool(Name = "refuse", Title = "Refuse", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Always refuses with a Sample sentence.")]
    public string Refuse() => throw new McpException("Pick a date, e.g. 2026-11-10.");

    [McpServerTool(Name = "domain_refuse", Title = "Domain refuse", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Always refuses with a Sample domain rule.")]
    public string DomainRefuse() => throw new InvalidOperationException("Only the owner can change this note.");

    [McpServerTool(Name = "crash", Title = "Crash", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Always fails unexpectedly in Sample.")]
    public string Crash() => throw new NullReferenceException("connection string = secret");

    [McpServerTool(Name = "cyrillic_row", Title = "Cyrillic row", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns a Sample row with Cyrillic text and a null field.")]
    public Row CyrillicRow() => new("Привет", null, Shade.DarkBlue);

    [McpServerTool(Name = "cyrillic_text", Title = "Cyrillic text", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns Sample JSON text it serialized itself with the studio options.")]
    public string CyrillicText() => JsonSerializer.Serialize(new { name = "Сеть", speed = 1396.19 }, StudioMcpJson.CamelCase);

    [McpServerTool(Name = "big", Title = "Big", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a Sample text of the given length.")]
    public string Big([Description("How many characters, 1 to 100000.")][Range(1, 100_000)] int chars) => new('x', chars);

    [McpServerTool(Name = "paged", Title = "Paged", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the Sample page it was asked for.")]
    public int Paged([Description("Which page. The first when omitted.")] int page = 1) => page;

    [McpServerTool(Name = "write_big", Title = "Write big", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Writes a Sample record and answers a text of the given length.")]
    public string WriteBig([Description("How many characters.")][Range(1, 100_000)] int chars) => new('x', chars);

    [McpServerTool(Name = "unannotated")]
    [Description("A Sample tool that forgot its annotations.")]
    public string Unannotated()
    {
        _counter.Hit();
        return "ok";
    }
}
