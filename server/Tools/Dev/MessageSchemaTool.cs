using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Networking;
using Turbo.Primitives.Networking.Revisions;
using Turbo.Primitives.Players.Permissions;
using Turbo.Primitives.Plugins.Messages;

namespace TurboMcp.Tools.Dev;

public sealed record MessageSchemaInput(
    [Description(
        "A plugin message's name (skills.state) or a core packet's (RoomEntryInfoMessageEvent, ChatMessage...), whole or a part of it, in any case."
    )]
    [StringLength(100)]
        string? Name = null,
    [Description("A header, the same both ways for a plugin message.")]
    [Range(0, 32767)]
        int? Header = null,
    [Description("How many to answer.")] [Range(1, 50)] int Take = 10
);

public sealed record MessageSchemaOutput(IReadOnlyList<MessageSchemaItem> Messages);

/// <summary>
/// One message: <see cref="Kind"/> <c>plugin</c> (a named plugin message, both ways),
/// <c>incoming</c> (a core packet a client sends) or <c>outgoing</c> (one the server sends).
/// <see cref="Fields"/> in order: a plugin message's as the client is sent its schema (camelCase),
/// a core packet's as its C# record has them.
/// </summary>
public sealed record MessageSchemaItem(
    string Kind,
    string Name,
    int Header,
    IReadOnlyList<MessageField> Fields,
    string? Type
);

public sealed record MessageField(string Name, string Type);

/// <summary>For plugin authors: what a message carries and the header the hotel gave it.</summary>
[RequiresPermission(McpNodes.MESSAGE_SCHEMA)]
public sealed class MessageSchemaTool(IPluginMessageDirectory messages, IRevisionManager revisions)
    : McpTool<MessageSchemaInput, MessageSchemaOutput>
{
    public override string Name => "message_schema";

    public override string Title => "Message and packet schemas";

    public override string Description =>
        "Looks up messages by name (whole or part) or header: every live plugin message (its name, the header the hotel gave it and its fields as the client reads them), and the core packets of the hotel's protocol revision (incoming and outgoing, with their C# fields). Use to check what a plugin's client and server halves exchange, or which core packet carries what.";

    public override bool ReadOnly => true;

    /// <summary>For a developer's own hotel: not registered on a live one.</summary>
    public override bool DevelopmentOnly => true;

    public override Task<McpToolResult> HandleAsync(
        MessageSchemaInput input,
        McpToolContext ctx,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(input.Name) && input.Header is null)
            return Task.FromResult(
                McpToolResult.Error("Give a name (or a part of one) or a header.")
            );

        var found = Plugin()
            .Concat(Core())
            .Where(x =>
                (input.Header is null || x.Header == input.Header)
                && (
                    string.IsNullOrWhiteSpace(input.Name)
                    || x.Name.Contains(input.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                )
            )
            // An exact name first, then plugin messages, then the shortest names.
            .OrderByDescending(x =>
                string.Equals(x.Name, input.Name?.Trim(), StringComparison.OrdinalIgnoreCase)
            )
            .ThenBy(x => x.Kind == "plugin" ? 0 : 1)
            .ThenBy(x => x.Name.Length)
            .Take(input.Take)
            .ToList();

        return Task.FromResult(
            found.Count == 0
                ? McpToolResult.Error(
                    $"No plugin message or core packet {(input.Name is null ? "" : $"named like \"{input.Name}\" ")}{(input.Header is null ? "" : $"at header {input.Header}")}. Try a part of the name."
                )
                : Ok(new MessageSchemaOutput(found))
        );
    }

    private IEnumerable<MessageSchemaItem> Plugin() =>
        messages.Messages.Select(x => new MessageSchemaItem(
            "plugin",
            x.Name,
            x.Header,
            Fields(JsonDocument.Parse(x.Schema).RootElement),
            null
        ));

    private IEnumerable<MessageSchemaItem> Core()
    {
        if (revisions.GetRevision(revisions.DefaultRevisionId) is not { } revision)
            yield break;

        var events = typeof(IMessageEvent)
            .Assembly.GetTypes()
            .Where(t =>
                t is { IsClass: true, IsAbstract: false }
                && typeof(IMessageEvent).IsAssignableFrom(t)
            )
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var (header, parser) in revision.Parsers)
        {
            var name = parser.GetType().Name;

            name = name.EndsWith("Parser", StringComparison.Ordinal)
                ? name[..^"Parser".Length]
                : name;
            events.TryGetValue(name, out var type);

            yield return new MessageSchemaItem(
                "incoming",
                name,
                header,
                type is null ? [] : Properties(type),
                type?.FullName
            );
        }

        foreach (var (type, serializer) in revision.Serializers)
            yield return new MessageSchemaItem(
                "outgoing",
                type.Name,
                serializer.Header,
                Properties(type),
                type.FullName
            );
    }

    /// <summary>A plugin schema's <c>[name, type]</c> pairs, the type as its JSON.</summary>
    private static IReadOnlyList<MessageField> Fields(JsonElement schema) =>
        [
            .. schema
                .EnumerateArray()
                .Select(x => new MessageField(
                    x[0].GetString() ?? "",
                    x[1].ValueKind == JsonValueKind.String ? x[1].GetString()! : x[1].GetRawText()
                )),
        ];

    private static IReadOnlyList<MessageField> Properties(Type type) =>
        [
            .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
                .Select(p => new MessageField(p.Name, TypeName(p.PropertyType))),
        ];

    private static string TypeName(Type type) =>
        Nullable.GetUnderlyingType(type) is { } inner ? $"{TypeName(inner)}?"
        : type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>"
        : type.Name;
}
