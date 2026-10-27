using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools.Dev;

public sealed record SearchDocsInput(
    [Description(
        "What to find, in words or API names: \"furni logic walk to\", \"IRoomEntryPolicy redirect\", \"PluginMessage carried packet\"."
    )]
    [StringLength(200, MinimumLength = 2)]
        string Query,
    [Description("How many sections to answer.")] [Range(1, 20)] int Take = 8
);

public sealed record SearchDocsOutput(IReadOnlyList<DocHit> Hits);

/// <summary>For plugin authors: the server's own guides and AI kit, searched by words.</summary>
[RequiresPermission(McpNodes.SEARCH_DOCS)]
public sealed class SearchDocsTool(DevDocs docs) : McpTool<SearchDocsInput, SearchDocsOutput>
{
    public override string Name => "search_docs";

    public override string Title => "Search the Turbo docs";

    public override string Description =>
        "Searches the guides of the Turbo server this hotel runs (its docs/, the same version as the server) and its plugin AI kit's skills, by words or API names. Answers the best sections: file, heading, first line, and the lines around the best match. Use before writing plugin code against an API you have not seen; read a whole section with read_doc.";

    public override bool ReadOnly => true;

    /// <summary>For a developer's own hotel: not registered on a live one.</summary>
    public override bool DevelopmentOnly => true;

    public override Task<McpToolResult> HandleAsync(
        SearchDocsInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        if (!docs.Available)
            return Task.FromResult(
                McpToolResult.Error(
                    "This server ships no guides (plugin-sdk/docs beside its assemblies): it was built before them. Read turbo-cloud's docs/ instead."
                )
            );

        var hits = docs.Search(input.Query, input.Take);

        return Task.FromResult(
            hits.Count == 0
                ? McpToolResult.Error(
                    $"No guide holds \"{input.Query}\". Try fewer or other words, or an API name (IHotel, FurniLogic, PluginMessage)."
                )
                : Ok(new SearchDocsOutput(hits))
        );
    }
}
