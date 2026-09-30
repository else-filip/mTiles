using mTiles.Services.Agents.SessionLogs;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which lines of a CLI's own transcript are the conversation — what a terminal agent tile hands over
/// when its agent changes.
/// </summary>
/// <remarks>The lines are the shapes measured 2026-09-23 (Claude Code 2.1.274, codex 0.154.0), trimmed.
/// Somebody else's format, so pinned the way <c>AiAgentTests</c> pins the flags.</remarks>
public class TranscriptTurnTests
{
    [Fact]
    public void Claude_reads_what_the_user_typed_and_what_the_agent_wrote()
    {
        Assert.Equal(new TranscriptTurn(true, "fix the icon"),
            ClaudeSessionLog.TurnIn("""{"type":"user","message":{"role":"user","content":"fix the icon"}}"""));
        Assert.Equal(new TranscriptTurn(true, "fix the icon"),
            ClaudeSessionLog.TurnIn("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"fix the icon"},{"type":"image","source":{}}]}}"""));
        Assert.Equal(new TranscriptTurn(false, "Done."),
            ClaudeSessionLog.TurnIn("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}"""));
    }

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"ok"}]}}""")]
    [InlineData("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bash","input":{}}]}}""")]
    [InlineData("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hm"}]}}""")]
    [InlineData("""{"type":"user","isMeta":true,"message":{"role":"user","content":[{"type":"text","text":"[Image: source: /tmp/x.png]"}]}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""")]
    [InlineData("""{"type":"attachment"}""")]
    [InlineData("not json")]
    public void Claude_leaves_out_the_work_and_what_it_writes_of_its_own(string line) =>
        Assert.Null(ClaudeSessionLog.TurnIn(line));

    [Fact]
    public void Claude_keeps_a_message_that_merely_starts_with_a_tag() =>
        Assert.Equal(new TranscriptTurn(true, "<div> does not render"),
            ClaudeSessionLog.TurnIn("""{"type":"user","message":{"role":"user","content":"<div> does not render"}}"""));

    [Fact]
    public void An_odd_line_is_skipped_rather_than_failing_the_whole_transcript()
    {
        Assert.Null(ClaudeSessionLog.TurnIn("""{"type":7,"message":{"content":"x"}}"""));
        Assert.Null(ClaudeSessionLog.TurnIn("""{"type":"user","message":{"content":[{"type":"text","text":5}]}}"""));
        Assert.Null(CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":3}}"""));
    }

    [Fact]
    public void Codex_reads_both_event_shapes()
    {
        Assert.Equal(new TranscriptTurn(true, "say ok"),
            CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"text","text":"say ok"}]}}}"""));
        Assert.Equal(new TranscriptTurn(false, "ok"),
            CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","content":[{"type":"Text","text":"ok"}]}}}"""));
        Assert.Equal(new TranscriptTurn(true, "say ok"),
            CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":"user_message","message":"say ok"}}"""));
    }

    [Fact]
    public void Codex_leaves_out_what_was_sent_to_the_model()
    {
        Assert.Null(CodexSessionLog.TurnIn(
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<recommended_plugins>"}]}}"""));
        Assert.Null(CodexSessionLog.TurnIn(
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"ContextCompaction"}}}"""));
    }

    [Fact]
    public void Pi_reads_the_text_of_user_and_assistant_messages()
    {
        Assert.Equal(new TranscriptTurn(true, "co tam ?"),
            PiSessionLog.TurnIn("""{"type":"message","message":{"role":"user","content":[{"type":"text","text":"co tam ?"}]}}"""));
        Assert.Equal(new TranscriptTurn(false, "Hej!"),
            PiSessionLog.TurnIn("""{"type":"message","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hm"},{"type":"text","text":"Hej!"}]}}"""));
    }

    [Theory]
    [InlineData("""{"type":"session","version":3,"id":"a"}""")]
    [InlineData("""{"type":"model_change","modelId":"x"}""")]
    [InlineData("""{"type":"message","message":{"role":"toolResult","content":[{"type":"text","text":"ok"}]}}""")]
    [InlineData("""{"type":"message","message":{"role":"assistant","content":[{"type":"toolCall","name":"bash"}]}}""")]
    [InlineData("not json")]
    public void Pi_leaves_out_everything_that_is_not_somebody_speaking(string line) =>
        Assert.Null(PiSessionLog.TurnIn(line));

    [Fact]
    public async Task Opencode_reads_its_transcript_out_of_its_database()
    {
        var data = Path.Combine(Path.GetTempPath(), "mtiles-oc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "opencode"));
        var database = Path.Combine(data, "opencode", "opencode.db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    create table session (id text, directory text, time_created integer, time_updated integer);
                    create table message (id text, session_id text, time_created integer, data text);
                    create table part (id text, message_id text, session_id text, time_created integer, data text);
                    insert into session values ('ses_1', 'D:/w', 1, 3);
                    insert into message values ('m1', 'ses_1', 1, '{"role":"user"}');
                    insert into message values ('m2', 'ses_1', 2, '{"role":"assistant"}');
                    insert into part values ('p1', 'm1', 'ses_1', 1, '{"type":"text","text":"co to za projekt ?"}');
                    insert into part values ('p2', 'm1', 'ses_1', 1, '{"type":"text","text":"reminder","synthetic":true}');
                    insert into part values ('p3', 'm2', 'ses_1', 2, '{"type":"reasoning","text":"thinking"}');
                    insert into part values ('p4', 'm2', 'ses_1', 3, '{"type":"text","text":"To jest kurs."}');
                    """;
                command.ExecuteNonQuery();
            }

            var log = new OpenCodeSessionLog(_ => data);
            var turns = await log.ReadTranscriptAsync(null, "D:/w", "ses_1");

            Assert.Equal([new TranscriptTurn(true, "co to za projekt ?"), new TranscriptTurn(false, "To jest kurs.")], turns);
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch { /* a temp directory */ }
        }
    }
}
