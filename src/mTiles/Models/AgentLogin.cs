namespace mTiles.Models;

/// <summary>
/// How an AI CLI is logged in: the arguments that start its own login, and one sentence saying what the
/// user is about to be asked to do.
/// </summary>
/// <remarks>
/// <para>The CLI's own route and nothing of ours: the flow, the browser page and the file the token lands
/// in all stay the tool's, which is what keeps this working when a CLI changes where it stores a login.
/// An OAuth flow of our own would have to impersonate the CLI's client and write its credential format.</para>
/// <para>An empty <see cref="Arguments"/> is an answer, not a gap — agy has no <c>login</c> subcommand and
/// signs in when it is started bare.</para>
/// </remarks>
/// <param name="Arguments">What follows the binary's name.</param>
/// <param name="Instructions">What the user will see and what to do about it, for the header over the
/// terminal the login runs in.</param>
public sealed record AgentLogin(IReadOnlyList<string> Arguments, string Instructions);
