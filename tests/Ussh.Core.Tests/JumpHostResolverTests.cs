using Ussh.Core.Models;
using Ussh.Core.Ssh;

namespace Ussh.Core.Tests;

public class JumpHostResolverTests
{
    private static ServerProfile Server(string name, ServerProfile? via = null) =>
        new() { Name = name, Host = name, Username = "u", JumpHostId = via?.Id };

    [Fact]
    public void NoJumpHostGivesEmptyChain()
    {
        var a = Server("a");
        Assert.Empty(JumpHostResolver.Resolve(a, new[] { a }));
    }

    [Fact]
    public void ChainIsOutermostFirst()
    {
        var outer = Server("outer");
        var inner = Server("inner", outer);
        var target = Server("target", inner);

        var chain = JumpHostResolver.Resolve(target, new[] { target, inner, outer });

        Assert.Equal(new[] { "outer", "inner" }, chain.Select(c => c.Name));
    }

    [Fact]
    public void MissingJumpHostIsReported()
    {
        var target = Server("target");
        target.JumpHostId = Guid.NewGuid();

        var ex = Assert.Throws<JumpHostConfigurationException>(() => JumpHostResolver.Resolve(target, new[] { target }));
        Assert.Contains("no longer exists", ex.Message);
    }

    [Fact]
    public void LoopIsReported()
    {
        var a = Server("a");
        var b = Server("b", a);
        a.JumpHostId = b.Id;

        Assert.Throws<JumpHostConfigurationException>(() => JumpHostResolver.Resolve(a, new[] { a, b }));
    }

    [Fact]
    public void CandidatesThatWouldCreateALoopAreExcluded()
    {
        var a = Server("a");
        var b = Server("b", a);
        var c = Server("c", b);
        var other = Server("other");
        var all = new[] { a, b, c, other };

        Assert.False(JumpHostResolver.CanUseAsJumpHost(a.Id, a, all)); // itself
        Assert.False(JumpHostResolver.CanUseAsJumpHost(a.Id, c, all)); // c → b → a
        Assert.True(JumpHostResolver.CanUseAsJumpHost(a.Id, other, all));
        Assert.True(JumpHostResolver.CanUseAsJumpHost(c.Id, a, all));
    }
}
