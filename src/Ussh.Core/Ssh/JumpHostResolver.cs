using Ussh.Core.Models;

namespace Ussh.Core.Ssh;

public sealed class JumpHostConfigurationException : Exception
{
    public JumpHostConfigurationException(string message) : base(message) { }
}

/// <summary>Turns a server's <see cref="ServerProfile.JumpHostId"/> links into a connect chain.</summary>
public static class JumpHostResolver
{
    public const int MaxHops = 8;

    /// <summary>
    /// Returns the jump hosts to go through to reach <paramref name="target"/>, outermost
    /// (the one connected to directly) first. Empty when the server has no jump host.
    /// </summary>
    /// <exception cref="JumpHostConfigurationException">A jump host is missing, or the links form a loop.</exception>
    public static IReadOnlyList<ServerProfile> Resolve(ServerProfile target, IReadOnlyCollection<ServerProfile> servers)
    {
        var chain = new List<ServerProfile>();
        var seen = new HashSet<Guid> { target.Id };
        var current = target;
        while (current.JumpHostId is { } jumpId)
        {
            var jump = servers.FirstOrDefault(s => s.Id == jumpId)
                ?? throw new JumpHostConfigurationException($"The jump host for {current.DisplayName} no longer exists. Edit the server to choose another.");
            if (!seen.Add(jump.Id))
                throw new JumpHostConfigurationException($"Jump hosts form a loop at {jump.DisplayName}.");
            if (chain.Count == MaxHops)
                throw new JumpHostConfigurationException($"More than {MaxHops} jump hosts in a row.");
            chain.Add(jump);
            current = jump;
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// True if <paramref name="candidate"/> may be the jump host for <paramref name="serverId"/>:
    /// it isn't the server itself and doesn't (directly or indirectly) jump through it.
    /// </summary>
    public static bool CanUseAsJumpHost(Guid serverId, ServerProfile candidate, IReadOnlyCollection<ServerProfile> servers)
    {
        var current = candidate;
        for (var hops = 0; hops <= MaxHops; hops++)
        {
            if (current.Id == serverId)
                return false;
            if (current.JumpHostId is not { } next)
                return true;
            current = servers.FirstOrDefault(s => s.Id == next);
            if (current == null)
                return true;
        }
        return false;
    }
}
