using mTiles.Services.Phone.Remote;

namespace mTiles.ViewModels;

/// <summary>The database tile as a paired phone sees it: which databases this workspace's agents may
/// query, with their read-write switch, and the rest of what the bridge has found, to add.</summary>
/// <remarks>
/// Every change goes through the command the tile's own buttons run, so the skill is republished and the
/// grants move exactly as they do on the computer. Allowing writes is asked on the phone first — the
/// choice carries its warning — because it is a grant to an agent, not a view.
/// </remarks>
public partial class DatabaseTileViewModel : IRemoteViewTile
{
    private const string WriteWarning =
        "Let the agents in this workspace change this database? INSERT, UPDATE and DELETE run without asking. " +
        "DROP, TRUNCATE and ALTER stay blocked.";

    /// <inheritdoc />
    public long RemoteVersion
    {
        get
        {
            var hash = new HashCode();
            hash.Add(IsServiceRunning);
            hash.Add(ServiceError);
            hash.Add(StatusText);
            foreach (var db in WorkspaceDatabases)
            {
                hash.Add(db.Key);
                hash.Add(db.AllowModifications);
            }
            foreach (var db in AllDatabases)
            {
                hash.Add(db.Key);
                hash.Add(db.IsInWorkspace);
            }
            return hash.ToHashCode();
        }
    }

    /// <inheritdoc />
    public RemoteTileBody DescribeForRemote()
    {
        if (!IsServiceRunning)
            return new RemoteTileBody("list", new RemoteStatus("idle", EmptyStateMessage), List: new RemoteList([]));

        var chosen = WorkspaceDatabases.Select(db => new RemoteListItem(
            db.Key, db.Label, $"{db.Provider} · {db.Server}", Checked: true,
            Switch: new RemoteItemSwitch("Write", db.AllowModifications, WriteWarning))).ToList();
        var available = AllDatabases.Where(db => !db.IsInWorkspace).Select(db => new RemoteListItem(
            db.Key, db.Label, $"{db.Provider} · {db.Server}", Checked: false)).ToList();

        return new RemoteTileBody(
            "list",
            new RemoteStatus("idle", StatusText),
            List: new RemoteList(
            [
                new RemoteListSection("Agents in this workspace can query", chosen,
                    Empty: "None yet. Tick a database below."),
                new RemoteListSection("Found on this network", available,
                    Empty: "Nothing else was found. Add connections in Settings on the computer."),
            ]));
    }

    /// <inheritdoc />
    public Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command) => Task.FromResult<RemoteRefusal?>(command switch
    {
        RemoteItem { Act: "check" } check => ToggleFromRemote(check.ItemId),
        RemoteItem { Act: "switch" } flip => SwitchFromRemote(flip.ItemId),
        _ => "The database tile cannot do that from a phone.",
    });

    private string? ToggleFromRemote(string key)
    {
        if (WorkspaceDatabases.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            RemoveFromWorkspace(chosen);
            return null;
        }

        if (AllDatabases.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } found)
            return "That database is no longer found.";
        if (WorkspaceDatabases.Count >= MaxWorkspaceDatabases)
            return $"A workspace can hold at most {MaxWorkspaceDatabases} databases.";
        AddToWorkspace(found);
        return null;
    }

    private string? SwitchFromRemote(string key)
    {
        if (WorkspaceDatabases.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } chosen)
            return "Tick the database first.";
        ToggleModifications(chosen);
        return null;
    }
}
