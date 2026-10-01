using mTiles.Models;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// How each CLI is logged in, and which instances offer it — each row is somebody else's command line,
/// measured 2026-10-01 against the installed binaries.
/// </summary>
public class AgentLoginTests
{
    [Theory]
    [InlineData("claude", "auth login")]
    [InlineData("codex", "login")]
    [InlineData("grok", "login")]
    [InlineData("agy", "")] // no login subcommand: it signs in as it starts
    public void An_agent_logs_in_by_its_own_command(string agentId, string arguments)
    {
        var login = AiAgentCatalog.Find(agentId)!.Login;

        Assert.NotNull(login);
        Assert.Equal(arguments, string.Join(' ', login.Arguments));
        Assert.False(string.IsNullOrWhiteSpace(login.Instructions));
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void An_agent_nobody_measured_offers_no_login(string agentId) =>
        Assert.Null(AiAgentCatalog.Find(agentId)!.Login);

    [Fact]
    public void An_instance_on_a_provider_key_is_not_logged_in_from_here()
    {
        var agent = AiAgentCatalog.Find("claude")!;

        Assert.True(AgentLoginLaunch.Offers(agent, new AiAgentInstance { AgentId = "claude" }));
        Assert.False(AgentLoginLaunch.Offers(agent,
            new AiAgentInstance { AgentId = "claude", ApiAccountId = "openrouter-1" }));
    }

    [Fact]
    public void A_sign_in_that_is_gone_is_not_replaced_by_the_default_account()
    {
        var instance = new AiAgentInstance { AgentId = "claude", SignInId = "gone" };

        Assert.Null(AgentLoginLaunch.For(AiAgentCatalog.Find("claude")!, instance, new AppSettings(), "."));
    }

    [Fact]
    public void The_default_account_runs_the_login_with_nothing_added_to_the_environment()
    {
        var launch = AgentLoginLaunch.For(AiAgentCatalog.Find("claude")!,
            new AiAgentInstance { AgentId = "claude", Name = "Claude Code" }, new AppSettings(), ".");

        Assert.NotNull(launch);
        Assert.Empty(launch.Environment);
        Assert.Matches("auth'? '?login", launch.Arguments[^1]);
    }

    /// <summary>The instance's own variables win, as they do for the session — or the login lands in one
    /// directory and the session runs in another.</summary>
    [Fact]
    public void The_login_runs_with_the_instances_own_environment()
    {
        var instance = new AiAgentInstance { AgentId = "claude", Name = "Claude Code" };
        instance.ExtraEnv["CLAUDE_CONFIG_DIR"] = "/elsewhere";

        var launch = AgentLoginLaunch.For(AiAgentCatalog.Find("claude")!, instance, new AppSettings(), ".");

        Assert.Equal("/elsewhere", launch!.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(null, true, true, false, false)]   // the window closed by hand
    [InlineData(false, true, true, false, false)]  // the login failed
    [InlineData(true, false, true, false, false)]  // the tile was closed meanwhile
    [InlineData(true, true, false, false, false)]  // another instance was picked meanwhile
    [InlineData(true, true, true, true, false)]    // a turn is running
    public void A_tile_restarts_only_after_a_clean_login_it_can_still_use(
        bool? answer, bool stillOpen, bool sameInstance, bool busy, bool restarts) =>
        Assert.Equal(restarts, AgentLoginLaunch.RestartsAfter(answer, stillOpen, sameInstance, busy));
}
