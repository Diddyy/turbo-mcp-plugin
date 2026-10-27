using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Turbo.Contracts.Plugins.Mcp;
using Turbo.Primitives.Logging;
using Turbo.Primitives.Networking;
using Turbo.Primitives.Networking.Revisions;
using Turbo.Primitives.Packets;
using Turbo.Primitives.Players;
using Turbo.Primitives.Plugins;
using Turbo.Primitives.Plugins.Messages;
using Turbo.Primitives.Plugins.Messages.Snapshots;
using Turbo.Testing;
using TurboMcp.Tools.Dev;
using Xunit;

namespace TurboMcp.Tests;

/// <summary>
/// The tools a plugin author's assistant calls on their own hotel (turbo-cloud docs/turbo-boost.md):
/// each read-only with its own node, the server's guides searched and read by section, a failed
/// plugin's error answered as places to fix, a message's fields, a plugin's tables.
/// </summary>
public sealed class DevToolsTests : IDisposable
{
    private static readonly PlayerId DEV = new(1);

    private readonly string _sdk = Path.Combine(
        Path.GetTempPath(),
        "mcp-devdocs-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_sdk))
            Directory.Delete(_sdk, recursive: true);
    }

    [Fact]
    public async Task Every_dev_tool_is_read_only_and_offered_by_its_own_node()
    {
        var tools = Tools();
        var nodes = new[]
        {
            McpNodes.SEARCH_DOCS,
            McpNodes.READ_DOC,
            McpNodes.PLUGIN_ERRORS,
            McpNodes.SERVER_LOG,
            McpNodes.MESSAGE_SCHEMA,
            McpNodes.PLUGIN_TABLES,
        };

        Assert.Equal(
            [
                "message_schema",
                "plugin_errors",
                "plugin_tables",
                "read_doc",
                "search_docs",
                "server_log",
            ],
            (await new McpToolTester(tools).As(DEV, nodes).ReadOnly().ListAsync(Ct)).Order()
        );
        Assert.Equal(
            ["server_log"],
            await new McpToolTester(tools).As(DEV, McpNodes.SERVER_LOG).ListAsync(Ct)
        );
    }

    [Fact]
    public async Task Search_docs_ranks_the_section_with_every_word_first_and_read_doc_reads_it()
    {
        Write(
            "docs/plugin-wired.md",
            "# Wired boxes\n\nA plugin can add wired boxes.\n\n## Furni logic\n\nWalk the user to the furni with WalkToAsync.\n\n```\n# not a heading\n```\n"
        );
        Write(
            "docs/commands.md",
            "# Chat commands\n\nA command can walk a user? No: furni logic walks.\n"
        );
        Write(
            "ai/skills/turbo-furni-logic/SKILL.md",
            "---\nname: turbo-furni-logic\n---\n\n# Furni logic and the room's turn\n"
        );
        var mcp = new McpToolTester(
            new SearchDocsTool(new DevDocs(_sdk)),
            new ReadDocTool(new DevDocs(_sdk))
        ).As(DEV, McpNodes.SEARCH_DOCS, McpNodes.READ_DOC);

        var hits = (
            await mcp.CallAsync("search_docs", new { query = "furni logic walk", take = 3 }, Ct)
        )
            .AssertOk()
            .Data<SearchDocsOutput>()
            .Hits;

        Assert.Equal(
            ("docs/plugin-wired.md", "Furni logic", 5),
            (hits[0].Path, hits[0].Heading, hits[0].Line)
        );
        Assert.Contains("WalkToAsync", hits[0].Excerpt);
        (await mcp.CallAsync("search_docs", new { query = "zebra" }, Ct)).AssertError(
            "No guide holds"
        );

        var section = (
            await mcp.CallAsync("read_doc", new { path = "plugin-wired.md", heading = "furni" }, Ct)
        )
            .AssertOk()
            .Data<DocText>();

        Assert.Equal(["Wired boxes", "Furni logic"], section.Headings);
        Assert.StartsWith("## Furni logic", section.Text);
        Assert.Contains(
            "# Furni logic and the room's turn",
            (await mcp.CallAsync("read_doc", new { path = "turbo-furni-logic" }, Ct))
                .AssertOk()
                .Data<DocText>()
                .Text
        );
        (
            await mcp.CallAsync(
                "read_doc",
                new { path = "plugin-wired.md", heading = "Selectors" },
                Ct
            )
        ).AssertError("Its headings: Wired boxes; Furni logic");
    }

    [Fact]
    public async Task Plugin_errors_answers_each_compile_refused_and_client_error_as_a_place_with_its_log()
    {
        var plugins = new Plugins(
            Snapshot(
                "skills",
                "Skills",
                PluginStateType.Failed,
                "Does not compile: server/SkillBook.cs(42,9): CS0103 The name 'level' does not exist in the current context; server/Tasks/Run.cs(7,13): TPP005 Plugins may not start processes (Process.Start) (and 3 more)"
            ),
            Snapshot(
                "hello",
                "Hello",
                PluginStateType.Failed,
                "The client module of hello did not build:\nclient/src/index.tsx:3:22: Could not resolve \"lodash\""
            )
        );
        var log = new Log(
            Entry(1, LogLevel.Warning, "Skills:Skills.SkillBook", "Book empty"),
            Entry(2, LogLevel.Error, "Turbo.Plugins.PluginManager", "Plugin skills failed to load"),
            Entry(
                3,
                LogLevel.Error,
                "Turbo.Plugins.PluginManager",
                "Plugin skillsx failed to load"
            ),
            Entry(4, LogLevel.Information, "Skills:Skills.SkillBook", "Trained")
        );
        var mcp = new McpToolTester(new PluginErrorsTool(plugins, log)).As(
            DEV,
            McpNodes.PLUGIN_ERRORS
        );

        var skills = (await mcp.CallAsync("plugin_errors", new { key = "skills" }, Ct))
            .AssertOk()
            .Data<PluginErrorsOutput>();

        Assert.Equal(
            [
                new PluginDiagnostic(
                    "compile",
                    "server/SkillBook.cs",
                    42,
                    9,
                    "CS0103",
                    "The name 'level' does not exist in the current context"
                ),
                new PluginDiagnostic(
                    "refused",
                    "server/Tasks/Run.cs",
                    7,
                    13,
                    "TPP005",
                    "Plugins may not start processes (Process.Start)"
                ),
            ],
            skills.Diagnostics
        );
        Assert.Equal([2L, 1L], skills.Log.Select(x => x.Sequence));
        Assert.Equal(
            new PluginDiagnostic(
                "client",
                "client/src/index.tsx",
                3,
                22,
                null,
                "Could not resolve \"lodash\""
            ),
            Assert.Single(
                (await mcp.CallAsync("plugin_errors", new { key = "hello" }, Ct))
                    .AssertOk()
                    .Data<PluginErrorsOutput>()
                    .Diagnostics
            )
        );
        (await mcp.CallAsync("plugin_errors", new { key = "nope" }, Ct)).AssertError(
            "The keys are hello, skills"
        );
    }

    [Fact]
    public async Task Message_schema_finds_a_plugin_message_and_a_core_packet_by_name_or_header()
    {
        var messages = new MessageStub(
            new PluginMessageSnapshot
            {
                Name = "skills.state",
                Header = 32766,
                Schema =
                    """[["level","int"],["scores",["array",["object",[["name","string"]]]]]]""",
            }
        );
        var mcp = new McpToolTester(new MessageSchemaTool(messages, new RevisionStub())).As(
            DEV,
            McpNodes.MESSAGE_SCHEMA
        );

        var state = Assert.Single(
            (await mcp.CallAsync("message_schema", new { header = 32766 }, Ct))
                .AssertOk()
                .Data<MessageSchemaOutput>()
                .Messages
        );

        Assert.Equal(("plugin", "skills.state"), (state.Kind, state.Name));
        Assert.Equal(
            [
                new MessageField("level", "int"),
                new MessageField("scores", """["array",["object",[["name","string"]]]]"""),
            ],
            state.Fields
        );

        var core = Assert.Single(
            (await mcp.CallAsync("message_schema", new { name = "testscore" }, Ct))
                .AssertOk()
                .Data<MessageSchemaOutput>()
                .Messages
        );

        Assert.Equal(("outgoing", 1234), (core.Kind, core.Header));
        Assert.Equal(
            [new MessageField("Points", "Int32"), new MessageField("Names", "List<String>")],
            core.Fields
        );
    }

    [Fact]
    public async Task Plugin_tables_answers_the_plugins_tables_with_their_columns()
    {
        var plugins = new Plugins(Snapshot("skills", "Skills", PluginStateType.Loaded, null))
        {
            Data =
            [
                new PluginDataItem(PluginDataKind.Table, "skills_levels", 12),
                new PluginDataItem(PluginDataKind.Setting, "Skills:Rate", 1),
            ],
        };
        var columns = new Columns(
            new()
            {
                ["skills_levels"] =
                [
                    new TableColumn("player_id", "int(11)", false, "PRI", null, null),
                ],
            }
        );
        var mcp = new McpToolTester(new PluginTablesTool(plugins, columns)).As(
            DEV,
            McpNodes.PLUGIN_TABLES
        );

        var table = Assert.Single(
            (await mcp.CallAsync("plugin_tables", new { key = "skills" }, Ct))
                .AssertOk()
                .Data<PluginTablesOutput>()
                .Tables
        );

        Assert.Equal(
            ("skills_levels", 12L, "player_id"),
            (table.Name, table.Rows, Assert.Single(table.Columns).Name)
        );
        Assert.Equal(["skills_levels"], columns.Asked);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMcpTool[] Tools() =>
        [
            new SearchDocsTool(new DevDocs(_sdk)),
            new ReadDocTool(new DevDocs(_sdk)),
            new PluginErrorsTool(new Plugins(), new Log()),
            new ServerLogTool(new Log()),
            new MessageSchemaTool(new MessageStub(), new RevisionStub()),
            new PluginTablesTool(new Plugins(), new Columns([])),
        ];

    private void Write(string path, string text)
    {
        var file = Path.Combine(_sdk, path);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private static PluginSnapshot Snapshot(
        string key,
        string name,
        PluginStateType state,
        string? error
    ) => new(key, name, "1.0.0", null, key, true, state, error, [], [], [], [], [], null);

    private static ServerLogEntry Entry(long n, LogLevel level, string category, string message) =>
        new(n, DateTime.UnixEpoch, level, category, message, null);

    private sealed class Plugins(params PluginSnapshot[] plugins) : IPluginAdministration
    {
        public ImmutableArray<PluginDataItem> Data { get; init; } = [];

        public bool UploadEnabled => false;

        public long MaxUploadBytes => 0;

        public ImmutableArray<PluginSnapshot> List() => [.. plugins];

        public PluginContributions? ContributionsOf(string key) => null;

        public Task<PluginUninstallPlan?> PlanUninstallAsync(string key, CancellationToken ct) =>
            Task.FromResult(
                plugins.FirstOrDefault(x => x.Key == key) is { } plugin
                    ? new PluginUninstallPlan(plugin, [], [], Data, [], null)
                    : null
            );

        public Task<PluginActionResult?> ReloadAsync(string key, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginActionResult> RescanAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PluginActionResult?> SetEnabledAsync(
            string key,
            bool enabled,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<PluginActionResult> InstallAsync(
            Stream package,
            Func<PluginPackage, string?> refuse,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public ImmutableArray<string> VersionsOf(string key) => [];

        public Task<PluginActionResult?> RollbackAsync(
            string key,
            string version,
            Func<PluginPackage, string?> refuse,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<PluginActionResult?> UninstallAsync(
            string key,
            PluginUninstallMode mode,
            PlayerId actor,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> ExportDataAsync(string key, Stream output, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class Log(params ServerLogEntry[] entries) : IServerLog
    {
        public int Capacity => 100;

        public ServerLogEntry? LastError => entries.LastOrDefault(x => x.Level >= LogLevel.Error);

        public IReadOnlyList<ServerLogEntry> Recent(
            int take,
            LogLevel minimum = LogLevel.Information,
            string? contains = null
        ) => [.. entries.Reverse().Where(x => x.Level >= minimum).Take(take)];
    }

    private sealed class MessageStub(params PluginMessageSnapshot[] messages)
        : IPluginMessageDirectory
    {
        public ImmutableArray<PluginMessageSnapshot> Messages => [.. messages];

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool TryGetHeader(string name, out int header)
        {
            header = 0;

            return false;
        }

        public IDisposable Register(string owner, IReadOnlyCollection<Type> messageTypes) =>
            throw new NotSupportedException();
    }

    public sealed record TestScoreComposer(int Points, List<string> Names) : IComposer;

    private sealed class Serializer : ISerializer
    {
        public int Header => 1234;

        public IServerPacket Serialize(IComposer message) => throw new NotSupportedException();
    }

    private sealed class RevisionStub : IRevisionManager, IRevision
    {
        public IDictionary<string, IRevision> Revisions =>
            new Dictionary<string, IRevision> { ["test"] = this };

        public string DefaultRevisionId => "test";

        public string Revision => "test";

        public IDictionary<int, IParser> Parsers { get; } = new Dictionary<int, IParser>();

        public IDictionary<Type, ISerializer> Serializers { get; } =
            new Dictionary<Type, ISerializer> { [typeof(TestScoreComposer)] = new Serializer() };

        public IRevision? GetRevision(string revisionName) => this;

        public void RegisterRevision(IRevision revision) { }
    }

    private sealed class Columns(Dictionary<string, IReadOnlyList<TableColumn>> columns)
        : ITableColumns
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyDictionary<string, IReadOnlyList<TableColumn>>> OfAsync(
            IReadOnlyCollection<string> tables,
            CancellationToken ct
        )
        {
            Asked.AddRange(tables);

            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TableColumn>>>(
                columns
            );
        }
    }
}
