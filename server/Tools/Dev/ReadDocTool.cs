using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools.Dev;

public sealed record ReadDocInput(
    [Description(
        "The guide: a path search_docs answered (docs/plugin-wired.md), its file name (plugin-wired.md) or a skill's name (turbo-furni-logic)."
    )]
    [StringLength(200, MinimumLength = 1)]
        string Path,
    [Description(
        "Only the section whose heading holds this text. Leave it out for the whole guide."
    )]
    [StringLength(200)]
        string? Heading = null
);

/// <summary>For plugin authors: one of the server's guides, or one section of it.</summary>
[RequiresPermission(McpNodes.READ_DOC)]
public sealed class ReadDocTool(DevDocs docs) : McpTool<ReadDocInput, DocText>
{
    public override string Name => "read_doc";

    public override string Title => "Read a Turbo guide";

    public override string Description =>
        "Reads one guide of the Turbo server this hotel runs (or one AI kit skill), whole or the section whose heading holds the text given, with the list of its section headings. Use after search_docs.";

    public override bool ReadOnly => true;

    /// <summary>For a developer's own hotel: not registered on a live one.</summary>
    public override bool DevelopmentOnly => true;

    public override Task<McpToolResult> HandleAsync(
        ReadDocInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        if (docs.Read(input.Path, input.Heading) is { } text)
            return Task.FromResult(Ok(text));

        var name = System.IO.Path.GetFileNameWithoutExtension(input.Path);
        var near = docs
            .Paths.Where(x => x.Contains(name, System.StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        if (docs.Read(input.Path, null) is { } whole)
            return Task.FromResult(
                McpToolResult.Error(
                    $"{whole.Path} has no section whose heading holds \"{input.Heading}\". Its headings: {string.Join("; ", whole.Headings)}."
                )
            );

        return Task.FromResult(
            McpToolResult.Error(
                near.Count > 0
                    ? $"There is no guide \"{input.Path}\". Did you mean {string.Join(", ", near)}?"
                    : $"There is no guide \"{input.Path}\". Find one with search_docs."
            )
        );
    }
}
