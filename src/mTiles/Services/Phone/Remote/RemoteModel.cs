using System.Text.Json.Serialization;
using mTiles.ViewModels;

namespace mTiles.Services.Phone.Remote;

// What a phone is told about this application, as plain records. Everything here is a snapshot taken
// on the UI thread and serialised off it, so nothing in it may reach back into a view model: a record
// holding a live collection would be walked by the serialiser on a thread the collection does not
// belong to.
//
// The wire is the page's contract as much as ours — the page is hosted separately and may be newer or
// older than this build — so a field is added freely and never renamed or repurposed. What changes the
// meaning of a field is a new PhoneProtocol.Version.

/// <summary>One workspace as the phone's outermost view lists it.</summary>
/// <param name="Loaded">Whether its tiles exist in this session. A workspace nobody has opened has no
/// layout to show until it is opened, which switches the desktop to it.</param>
/// <param name="Current">Whether it is the one on the desktop's screen.</param>
public sealed record RemoteWorkspace(
    string Id,
    string Name,
    string? Branch,
    string Activity,
    bool Loaded,
    bool Current,
    bool Favorite);

/// <summary>A workspace's tile tree, drawn by the phone as a miniature of the real one.</summary>
public sealed record RemoteLayout(string WorkspaceId, string Name, RemoteNode? Root);

/// <summary>One node of <see cref="RemoteLayout"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RemoteSplit), "split")]
[JsonDerivedType(typeof(RemoteLeaf), "leaf")]
public abstract record RemoteNode;

/// <param name="Direction"><c>row</c> when the two children stand side by side, <c>column</c> when one is
/// above the other — the words CSS flexbox uses, since that is what draws it.</param>
/// <param name="Ratio">The first child's share, 0–1. A side held at a size in pixels on the desktop
/// is sent as the share it happens to take now, because the phone is not the same size.</param>
public sealed record RemoteSplit(string Direction, double Ratio, RemoteNode First, RemoteNode Second) : RemoteNode;

/// <param name="Reachable">Whether the phone can zoom into it. A tile it cannot is still drawn — the
/// layout would not be recognisable with holes in it — but dimmed and not tappable.</param>
/// <param name="Preview">What the miniature's card says about the tile without zooming in, or null for a
/// kind with nothing to say.</param>
public sealed record RemoteLeaf(
    string TileId,
    string Kind,
    string Name,
    string Activity,
    bool Active,
    bool Reachable,
    RemotePreview? Preview = null) : RemoteNode;

/// <summary>A tile at a glance, as its card in the miniature draws it.</summary>
/// <param name="Text">One line: what it is doing, what it is asking, or what it last said.</param>
/// <param name="ContextPercent">How full an agent's context is, 0–100, where known.</param>
/// <param name="ChangedAt">When the tile last did something, in Unix milliseconds on this machine's clock —
/// the page corrects for its own with the <c>now</c> the hello answer carries. Null where nothing
/// knows.</param>
public sealed record RemotePreview(string? Text, double? ContextPercent = null, long? ChangedAt = null);

/// <summary>What a tile says about itself for its card — see <see cref="RemotePreview"/>.</summary>
/// <param name="ChangedAt">When the tile knows it — a conversation's last entry — or null, and the bridge
/// then stamps the moment it sees <paramref name="Text"/> change.</param>
public sealed record TilePreview(string? Text, double? ContextPercent = null, DateTimeOffset? ChangedAt = null);

/// <summary>One tile, zoomed into.</summary>
/// <param name="View">Which body the page draws: <c>chat</c>, <c>terminal</c>, or <c>none</c> for a
/// tile that offers only actions.</param>
/// <param name="ListsConversations">Whether the tile can be asked for the other conversations it could show
/// (<see cref="ViewModels.IRemoteConversationsTile"/>), so the page offers them only where they exist.</param>
public sealed record RemoteTileView(
    string TileId,
    string WorkspaceId,
    string Name,
    string Kind,
    string View,
    RemoteStatus Status,
    RemoteChat? Chat,
    RemoteScreen? Screen,
    RemoteComposer Composer,
    IReadOnlyList<RemoteAction> Actions,
    string? NewLabel = null,
    bool ListsConversations = false,
    RemoteList? List = null);

/// <summary>What the content of a tile says about itself — the part of <see cref="RemoteTileView"/>
/// the tile knows and its leaf does not.</summary>
/// <param name="NewLabel">What starting the tile over is called — "New goal", "New conversation" — when a
/// phone may ask for it, or null. The page asks before it sends it, because it is showing what would be
/// left behind.</param>
public sealed record RemoteTileBody(
    string View,
    RemoteStatus Status,
    RemoteChat? Chat = null,
    RemoteScreen? Screen = null,
    RemoteComposer? Composer = null,
    string? NewLabel = null,
    RemoteList? List = null);

/// <param name="Text">One line saying what the tile is doing, or null.</param>
/// <param name="Detail">What it runs on — an agent's model, a goal's phase — or null.</param>
/// <param name="ContextPercent">How full the model's context is, 0–100, or null where not known.</param>
public sealed record RemoteStatus(string Activity, string? Text = null, string? Detail = null,
    double? ContextPercent = null);

/// <param name="Omitted">How many older items were left out; the page says so rather than showing a
/// conversation that seems to begin half way through.</param>
public sealed record RemoteChat(
    IReadOnlyList<RemoteChatItem> Items,
    int Omitted,
    RemotePending? Pending,
    IReadOnlyList<RemotePlanStep>? Plan);

/// <param name="Role"><c>user</c>, <c>assistant</c>, <c>system</c>, <c>work</c>, <c>plan</c>,
/// <c>notice</c> or <c>questions</c>.</param>
/// <param name="Markdown">Whether <paramref name="Text"/> is the tool's own markdown. Text this
/// application composed is plain, and its columns are made of spaces.</param>
/// <param name="Tone"><c>info</c>, <c>warning</c>, <c>error</c> or <c>summary</c>; null for none.</param>
public sealed record RemoteChatItem(
    string Id,
    string Role,
    string Text,
    bool Markdown = false,
    bool Streaming = false,
    IReadOnlyList<RemoteWorkLine>? Work = null,
    string? Tone = null);

/// <param name="State"><c>running</c>, <c>done</c>, <c>failed</c> or <c>declined</c>.</param>
public sealed record RemoteWorkLine(string Title, string State);

/// <summary>What a tile is waiting for somebody to answer.</summary>
/// <param name="Kind"><c>approval</c>, <c>questions</c>, <c>plan</c> or <c>gate</c>.</param>
/// <param name="Id">What the answer is sent back under, so an answer to a request that has since been
/// replaced is refused rather than applied to the next one.</param>
/// <param name="SecondsLeft">For a countdown, how long before the tile carries on by itself.</param>
public sealed record RemotePending(
    string Kind,
    string Id,
    string Title,
    string? Detail,
    IReadOnlyList<RemoteOption> Options,
    IReadOnlyList<RemoteQuestion>? Questions = null,
    int? SecondsLeft = null);

/// <param name="Tone"><c>primary</c>, <c>neutral</c> or <c>danger</c>.</param>
public sealed record RemoteOption(string Id, string Label, string Tone);

public sealed record RemoteQuestion(
    string Id,
    string? Header,
    string Text,
    IReadOnlyList<string> Options,
    bool Multi,
    bool Custom);

/// <param name="Status"><c>pending</c>, <c>running</c> or <c>done</c>.</param>
public sealed record RemotePlanStep(string Text, string Status);

/// <summary>What a tile made of rows shows a phone — the git tile's changed files, the database tile's
/// databases — for the view <c>list</c>.</summary>
/// <param name="Detail">What the selected row opens onto, drawn under the list: a file's diff.</param>
public sealed record RemoteList(IReadOnlyList<RemoteListSection> Sections, RemoteDetail? Detail = null);

/// <param name="CheckAll">Whether the heading carries a tick for every row in it, and whether it is ticked;
/// null for none.</param>
/// <param name="Empty">What the section says when it has no rows.</param>
public sealed record RemoteListSection(string Title, IReadOnlyList<RemoteListItem> Items, bool? CheckAll = null,
    string? Empty = null);

/// <param name="Checked">A tick in front of the row, and whether it is ticked; null for none. Only the tick
/// itself flips it — a tap on the row never does, because a tick can be a grant.</param>
/// <param name="Badge">A short mark before the text — a git status letter.</param>
/// <param name="Selectable">Tapping the row opens its <see cref="RemoteList.Detail"/>.</param>
public sealed record RemoteListItem(string Id, string Text, string? Note = null, bool? Checked = null,
    string? Badge = null, RemoteItemSwitch? Switch = null, bool Selectable = false, bool Selected = false);

/// <param name="Warning">What the phone asks before turning it on, or null.</param>
public sealed record RemoteItemSwitch(string Label, bool On, string? Warning = null);

/// <param name="Kind"><c>diff</c> or <c>text</c>.</param>
public sealed record RemoteDetail(string Title, string Text, string Kind);

/// <summary>The terminal's screen as text — the last frame, not the stream that drew it.</summary>
public sealed record RemoteScreen(IReadOnlyList<string> Lines, string? Title);

public sealed record RemoteAction(string Id, string Label, string Icon, bool Enabled);

/// <param name="Enabled">Whether text can be sent at all right now.</param>
/// <param name="Keys">Whether the arrow keys and Escape mean anything here — a terminal, not a chat.</param>
/// <param name="CanInterrupt">Whether a Stop is offered.</param>
/// <param name="Draft">What is typed, unsent, in the tile's own input on the computer — a sentence
/// dictated there, or something left half written — so the phone can show it, edit it and send it
/// instead of being refused because of a draft it cannot see. Null where the tile has none.</param>
/// <param name="Modes">The other ways a message can be sent here besides the plain one — the Goal tile's
/// Set goal &amp; run, Review, Detect goal — or null.</param>
/// <param name="StartsOver">Whether sending now replaces what the tile holds (a finished goal's
/// transcript), so the page asks first and says it did.</param>
/// <param name="Pickers">What the next message runs as — model, permission mode, effort — each with the
/// value it has now and what it can be changed to; null for a tile with none.</param>
/// <param name="CanCompact">Whether the agent can be asked, now, to summarise its context.</param>
/// <param name="SyncsDraft">Whether what is typed on the phone may be mirrored into the tile's own input as
/// it is typed (<see cref="RemoteDraft"/>).</param>
public sealed record RemoteComposer(bool Enabled, string Placeholder, bool Keys = false, bool CanInterrupt = false,
    string? Draft = null, IReadOnlyList<RemoteSendMode>? Modes = null, bool StartsOver = false,
    IReadOnlyList<RemotePicker>? Pickers = null, bool CanCompact = false, bool SyncsDraft = false,
    bool TakesAttachments = false)
{
    public static readonly RemoteComposer None = new(false, "");
}

/// <summary>One of the composer's settings, as the phone offers it.</summary>
/// <param name="Id"><c>model</c>, <c>mode</c> or <c>effort</c>.</param>
/// <param name="Value">The id of what it is set to now, or null where nothing is known yet.</param>
/// <param name="Custom">Whether a value not in <paramref name="Choices"/> may be typed — a model name.</param>
public sealed record RemotePicker(string Id, string Label, string? Value, string? ValueLabel,
    IReadOnlyList<RemoteChoice> Choices, bool Custom = false);

/// <param name="Warning">A sentence the phone asks the user to agree to before choosing it — bypassing
/// every permission check — or null.</param>
public sealed record RemoteChoice(string Id, string Label, string? Warning = null);

/// <summary>One stored conversation a tile can be pointed at.</summary>
/// <param name="Reason">Why it cannot be opened from here, or null.</param>
public sealed record RemoteConversation(string Id, string Title, string Note, bool Current, string? Reason);

/// <summary>One way of sending the composer's text other than the plain one.</summary>
/// <param name="NeedsText">True when it sends what is typed, false when it works without any (a goal read
/// from the changes), null when either will do.</param>
public sealed record RemoteSendMode(string Id, string Label, bool? NeedsText, bool Enabled = true);

/// <summary>Something a phone asks a tile to do.</summary>
public abstract record RemoteTileCommand;

/// <param name="Submit">Whether it is sent — Enter after it — or only put in the tile's input.</param>
/// <param name="Mode">One of the composer's <see cref="RemoteComposer.Modes"/>, or null for the plain send.</param>
/// <param name="Replaces">The computer's draft as the phone was last shown it: a draft still exactly that
/// is the phone's to replace, since the user has seen it; anything else is refused as in the way.</param>
/// <param name="Discard">The phone asked, and the user agreed, that this may replace what the tile holds
/// (<see cref="RemoteComposer.StartsOver"/>).</param>
public sealed record RemoteSendText(string Text, bool Submit, string? Mode = null, string? Replaces = null,
    bool Discard = false) : RemoteTileCommand;

/// <summary>Starts the tile over — a new goal, a new conversation — which the phone has already asked
/// about.</summary>
public sealed record RemoteNewConversation : RemoteTileCommand;

/// <summary>What is typed on the phone, mirrored into the tile's own input as it is typed — sent nowhere.</summary>
/// <param name="Seen">The tile's draft as the phone was last shown it; see <see cref="RemoteSendText.Replaces"/>.</param>
public sealed record RemoteDraft(string Text, string? Seen) : RemoteTileCommand;

/// <summary>Sets one of the composer's <see cref="RemoteComposer.Pickers"/>. A choice carrying a warning
/// arrives only after the phone has asked.</summary>
public sealed record RemotePick(string Picker, string Value) : RemoteTileCommand;

/// <summary>Asks the agent to summarise its context and carry on from the summary; asked on the phone.</summary>
public sealed record RemoteCompact : RemoteTileCommand;

/// <summary>Something done to one row of a <see cref="RemoteList"/>.</summary>
/// <param name="Act"><c>check</c> (flip its tick — <c>*</c> for every row), <c>select</c> (open it) or
/// <c>switch</c> (flip its switch).</param>
public sealed record RemoteItem(string ItemId, string Act) : RemoteTileCommand;

/// <summary>Points the tile at another of its stored conversations.</summary>
public sealed record RemoteOpenConversation(string ConversationId) : RemoteTileCommand;

public sealed record RemoteKey(TileKey Key) : RemoteTileCommand;

/// <summary>One of the options of the pending request named <paramref name="PendingId"/>.</summary>
public sealed record RemoteChoose(string PendingId, string OptionId) : RemoteTileCommand;

/// <summary>Answers to a round of questions, per question id.</summary>
public sealed record RemoteAnswer(string PendingId, IReadOnlyDictionary<string, IReadOnlyList<string>> Answers)
    : RemoteTileCommand;

public sealed record RemoteInterrupt : RemoteTileCommand;

/// <summary>The words the wire uses for a tile's activity.</summary>
public static class RemoteActivity
{
    public static string Of(Models.TileActivity activity) => activity switch
    {
        Models.TileActivity.Idle => "idle",
        Models.TileActivity.Working => "working",
        Models.TileActivity.Blocked => "blocked",
        _ => "unknown",
    };
}
