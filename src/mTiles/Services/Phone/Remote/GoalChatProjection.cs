using System.Text;
using mTiles.Models;

namespace mTiles.Services.Phone.Remote;

/// <summary>
/// A Goal tile's transcript as a phone draws it.
/// </summary>
/// <remarks>
/// Pure, over the same <see cref="GoalMessage"/>s the tile's transcript binds to. A review's findings
/// travel as one line each — severity, place, title — because a finding's detail is for reading at the
/// desk, and the sofa's question is only whether the run is going well. What the tile is waiting on is
/// built by the tile itself (<see cref="RemotePending"/>), since only it knows which of its blocks is
/// showing.
/// </remarks>
public static class GoalChatProjection
{
    public const int MaxItems = 60;

    public static RemoteChat Project(IReadOnlyList<GoalMessage> messages, RemotePending? pending,
        int maxItems = MaxItems)
    {
        var skip = Math.Max(0, messages.Count - maxItems);
        var items = new List<RemoteChatItem>(messages.Count - skip);

        for (var i = skip; i < messages.Count; i++)
            items.Add(Item(messages[i], i));

        return new RemoteChat(items, skip, pending, null);
    }

    private static RemoteChatItem Item(GoalMessage message, int index)
    {
        // The position is the id. A goal message has none of its own, and the transcript only ever grows
        // at the end — a new goal clears it, which the page sees as every id changing at once.
        var id = $"m{index}";

        if (message.HasQuestions)
            return new RemoteChatItem(id, "questions", Questions(message.Questions));

        var text = AgentChatProjection.Clip(message.Text) ?? "";
        if (message.HasFindings)
            text = WithFindings(text, message.Findings);

        return message.Role switch
        {
            GoalMessageRole.User => new RemoteChatItem(id, "user", text),
            GoalMessageRole.Assistant => new RemoteChatItem(id, "assistant", text, Markdown: message.IsMarkdown),
            _ => new RemoteChatItem(id, "system", text, Tone: message.IsRunSummary ? "summary" : null),
        };
    }

    private static string WithFindings(string head, IReadOnlyList<GoalFinding> findings)
    {
        var sb = new StringBuilder(head.TrimEnd());
        foreach (var finding in findings)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(finding.Severity.ToString().ToLowerInvariant());
            if (finding.File.Length > 0)
            {
                sb.Append("  ").Append(finding.File);
                if (finding.Line is { } line) sb.Append(':').Append(line);
            }
            sb.Append("\n  ").Append(finding.Title);
        }
        return sb.ToString();
    }

    private static string Questions(IReadOnlyList<GoalQuestion> questions)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < questions.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(i + 1).Append(". ").Append(questions[i].Question);
            sb.Append("\n→ ").Append(questions[i].Answer.Trim().Length > 0 ? questions[i].Answer.Trim() : "(no answer)");
        }
        return sb.ToString();
    }
}
