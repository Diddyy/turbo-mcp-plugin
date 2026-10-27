using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Catalog.Providers;
using Turbo.Primitives.Catalog.Tags;
using Turbo.Primitives.Players.Permissions;

namespace TurboMcp.Tools;

public sealed record CatalogSearchInput(
    [Description("Words in an offer's or a page's name, such as throne or rare.")]
    [StringLength(64, MinimumLength = 2)]
        string Text,
    [Description("How many offers to answer.")] [Range(1, 50)] int Take = 20
);

public sealed record CatalogSearchOutput(
    IReadOnlyList<FoundOffer> Offers,
    IReadOnlyList<FoundPage> Pages
);

public sealed record FoundOffer(
    int Id,
    string Name,
    int PageId,
    string Page,
    int CostCredits,
    int CostCurrency,
    int? CurrencyType,
    bool Visible
);

public sealed record FoundPage(int Id, string Name, bool Visible);

/// <summary>The live catalog's offers and pages whose names hold the words.</summary>
[RequiresPermission(McpNodes.CATALOG_SEARCH)]
public sealed class CatalogSearchTool(ICatalogSnapshotProvider<NormalCatalog> catalog)
    : McpTool<CatalogSearchInput, CatalogSearchOutput>
{
    public override string Name => "catalog_search";

    public override string Title => "Search the catalog";

    public override string Description =>
        "Searches the live catalog for offers and pages whose names hold the given text (any case): each offer's id, name, page, price in credits and in another currency (its activity point type), and whether it is shown.";

    public override bool ReadOnly => true;

    public override Task<McpToolResult> HandleAsync(
        CatalogSearchInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        var snapshot = catalog.Current;
        var text = input.Text.Trim();
        var pages = snapshot.PagesById;

        string PageName(int id) =>
            pages.TryGetValue(id, out var page) ? page.Name ?? page.Localization : $"#{id}";

        var offers = snapshot
            .OffersById.Values.Where(x =>
                x.LocalizationId.Contains(text, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(x => x.LocalizationId, StringComparer.Ordinal)
            .Take(input.Take)
            .Select(x => new FoundOffer(
                x.Id,
                x.LocalizationId,
                x.PageId,
                PageName(x.PageId),
                x.CostCredits,
                x.CostCurrency,
                x.ActivityPointType,
                x.Visible
            ))
            .ToList();
        var found = pages
            .Values.Where(x =>
                (x.Name ?? x.Localization).Contains(text, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(x => x.Id)
            .Take(input.Take)
            .Select(x => new FoundPage(x.Id, x.Name ?? x.Localization, x.Visible))
            .ToList();

        return Task.FromResult(
            offers.Count == 0 && found.Count == 0
                ? McpToolResult.Error(
                    $"No offer or page name holds \"{text}\". Try a shorter word."
                )
                : Ok(new CatalogSearchOutput(offers, found))
        );
    }
}
