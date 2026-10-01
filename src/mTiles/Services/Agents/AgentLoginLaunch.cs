using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Shells;

namespace mTiles.Services.Agents;

/// <summary>
/// One login, ready to run in a terminal: what to start, as which account, and what to tell the user.
/// </summary>
/// <remarks>
/// <para>Through the default shell's command form rather than the binary alone, for the reason
/// <see cref="IShellTerminal.Program"/> exists: npm's shims are <c>.cmd</c> files on Windows, and the path
/// this machine found is only something the shell knows how to run.</para>
/// <para>The account goes through the process environment and never the command line — the rule every
/// launch keeps — so the login lands in the directory of the sign-in the instance runs as, and the default
/// account sets nothing at all.</para>
/// </remarks>
public sealed record AgentLoginLaunch(
    string Title,
    string Instructions,
    string WorkingDirectory,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment)
{
    /// <summary>Whether a tile starts its agent again after a login: only a clean one, on a tile still open
    /// and still on the instance that was logged in, and never under a turn in progress — a busy session is
    /// plainly still authenticated, and restarting it would cut the turn off.</summary>
    public static bool RestartsAfter(bool? answer, bool stillOpen, bool sameInstance, bool busy) =>
        answer == true && stillOpen && sameInstance && !busy;

    /// <summary>Whether an instance can be logged in from here at all.</summary>
    /// <remarks>Not one that authenticates through a provider's key: its account is the key, and a CLI
    /// login under it would be a subscription nothing then runs as.</remarks>
    public static bool Offers(IAiAgent agent, AiAgentInstance instance) =>
        agent.Login is not null && string.IsNullOrEmpty(instance.ApiAccountId);

    /// <summary>The login for <paramref name="instance"/>, or null where it has none or its sign-in's
    /// directory cannot be made.</summary>
    public static AgentLoginLaunch? For(IAiAgent agent, AiAgentInstance instance, AppSettings settings,
        string workingDirectory)
    {
        if (!Offers(agent, instance)) return null;
        var launch = ForAccountOf(agent, instance, settings, workingDirectory);
        return launch is null ? null : launch with { Environment = WithExtraEnv(launch.Environment, instance) };
    }

    private static AgentLoginLaunch? ForAccountOf(IAiAgent agent, AiAgentInstance instance, AppSettings settings,
        string workingDirectory)
    {
        if (instance.SignInId is not { Length: > 0 } signInId)
            return Build(agent, $"{agent.DisplayName} — {instance.Name}", new Dictionary<string, string?>(),
                "", settings, workingDirectory);

        // A sign-in the row names and settings no longer hold is an instance the chooser already refuses
        // to run; logging into the default account in its place would be the wrong account said nowhere.
        return AiSignInStore.Find(settings, signInId) is { } signIn
            ? ForSignIn(agent, signIn, instance.Name, settings, workingDirectory)
            : null;
    }

    /// <summary>The instance's own variables merged last, the order <see cref="AiAgent.EnvFor"/> keeps — a
    /// <c>CLAUDE_CONFIG_DIR</c> or a proxy set there is where the session runs, so it is where the login has
    /// to land.</summary>
    private static IReadOnlyDictionary<string, string?> WithExtraEnv(
        IReadOnlyDictionary<string, string?> environment, AiAgentInstance instance)
    {
        var merged = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        foreach (var (name, value) in instance.ExtraEnv)
            merged[name] = value;
        return merged;
    }

    /// <summary>The login for one sign-in row — what Settings' Sign in runs, and what an instance on that
    /// sign-in runs from its tile — or null where the agent has none or the directory cannot be made.</summary>
    public static AgentLoginLaunch? ForSignIn(IAiAgent agent, AiSignIn signIn, string label, AppSettings settings,
        string workingDirectory)
    {
        if (agent.Login is null) return null;
        var directory = AiSignInStore.DirectoryFor(signIn);
        if (!AiSignInStore.Ensure(signIn, agent))
        {
            Trace.TraceWarning($"[Login] Could not create {directory}.");
            return null;
        }

        return Build(agent, $"{agent.DisplayName} — {label}", agent.SignInEnv(directory),
            $" The login is kept for \"{signIn.Name}\" only, in {directory}.", settings, workingDirectory);
    }

    private static AgentLoginLaunch Build(IAiAgent agent, string account,
        IReadOnlyDictionary<string, string?> environment, string accountNote, AppSettings settings,
        string workingDirectory)
    {
        var login = agent.Login!;
        var shell = ShellTerminalCatalog.ResolveDefault(settings);
        var command = InstallCommand.Line(agent.BinaryName, AiAgentCatalog.Locate(agent), login.Arguments, shell.Shell);
        var (executable, arguments) = shell.CommandLineFor(command);

        return new AgentLoginLaunch($"Sign in to {account}", login.Instructions + accountNote,
            workingDirectory, executable, arguments, environment);
    }
}
