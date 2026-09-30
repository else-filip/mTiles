using System.Net;
using System.Net.Sockets;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Database;
using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>What a paired phone sees of a database tile and may do to it: tick a database in or out of
/// what this workspace's agents may query, and flip its Write switch.</summary>
public sealed class DatabaseRemoteTests : IDisposable
{
    private readonly TempSettings _settings = new();
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mtiles-dbremote-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceAgentFiles _agentFiles;
    private DatabaseServiceManager? _manager;

    public DatabaseRemoteTests()
    {
        Directory.CreateDirectory(_dir);
        var db = _settings.Service.Settings.Database;
        db.Enabled = true;
        db.SqlServer.Enabled = false;
        db.PostgreSql.Enabled = false;
        _agentFiles = new WorkspaceAgentFiles(_dir);
        _agentFiles.Follow([AiAgentCatalog.Find("claude")!]);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    [Fact]
    public void A_stopped_service_is_an_empty_list_with_its_reason()
    {
        Ui.Run(() =>
        {
            _manager = new DatabaseServiceManager(_settings.Service);
            using var tile = Tile();

            var body = tile.DescribeForRemote();
            Assert.Empty(Assert.IsType<RemoteList>(body.List).Sections);
            Assert.Equal(tile.EmptyStateMessage, body.Status?.Text);
        });
    }

    [Fact]
    public void A_tick_adds_a_database_and_a_second_takes_it_out()
    {
        Ui.Run(() =>
        {
            StartWith("shop");
            using var tile = Tile();

            Assert.Null(Handle(tile, new RemoteItem("localhost/shop", "check")));
            Assert.Equal(["localhost/shop"], Chosen(tile));
            Assert.Null(Handle(tile, new RemoteItem("localhost/shop", "check")));
            Assert.Empty(Chosen(tile));
            Assert.Equal("That database is no longer found.", Handle(tile, new RemoteItem("localhost/gone", "check"))?.Message);
        });
    }

    [Fact]
    public void Write_needs_the_database_ticked_and_carries_its_warning()
    {
        Ui.Run(() =>
        {
            StartWith("shop");
            using var tile = Tile();

            Assert.Equal("Tick the database first.", Handle(tile, new RemoteItem("localhost/shop", "switch"))?.Message);
            Handle(tile, new RemoteItem("localhost/shop", "check"));
            Assert.Null(Handle(tile, new RemoteItem("localhost/shop", "switch")));

            var toggle = Assert.IsType<RemoteItemSwitch>(tile.DescribeForRemote().List!.Sections[0].Items[0].Switch);
            Assert.True(toggle.On);
            Assert.False(string.IsNullOrEmpty(toggle.Warning));
        });
    }

    [Fact]
    public void A_workspace_holds_at_most_ten_databases()
    {
        Ui.Run(() =>
        {
            var names = Enumerable.Range(0, 11).Select(i => $"db{i:00}").ToArray();
            StartWith(names);
            using var tile = Tile();

            foreach (var name in names[..10]) Assert.Null(Handle(tile, new RemoteItem($"localhost/{name}", "check")));
            Assert.Equal("A workspace can hold at most 10 databases.",
                Handle(tile, new RemoteItem("localhost/db10", "check"))?.Message);
        });
    }

    private static string[] Chosen(DatabaseTileViewModel tile) =>
        [.. tile.DescribeForRemote().List!.Sections[0].Items.Select(i => i.Id)];

    private static RemoteRefusal? Handle(DatabaseTileViewModel tile, RemoteTileCommand command) =>
        tile.HandleRemoteAsync(command).GetAwaiter().GetResult();

    private DatabaseTileViewModel Tile() => new(_dir, _settings.Service, _manager!, _agentFiles);

    private void StartWith(params string[] databases)
    {
        for (var attempt = 0; attempt < 5 && _manager is not { IsRunning: true }; attempt++)
        {
            _manager?.Dispose();
            _settings.Service.Settings.Database.HttpPort = FreePort();
            _manager = new DatabaseServiceManager(_settings.Service);
            _manager.Start();
        }
        Assert.True(_manager!.IsRunning, _manager.LastError);
        foreach (var name in databases)
            _manager.Registry.Register(new DatabaseInstance
            {
                Server = "localhost", Database = name, Provider = DbProviderType.PostgreSQL,
                ConnectionString = $"Host=localhost;Database={name}",
            });
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
