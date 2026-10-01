using System.Text.Json.Nodes;
using Ussh.Core.Models;
using Ussh.Core.Security;

namespace Ussh.Core.Tests;

public sealed class VaultTests : IDisposable
{
    // Low iteration count keeps the tests fast; production uses Vault.DefaultIterations.
    private const int TestIterations = 100_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ussh-tests-" + Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_dir, "vault.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RoundTripsServersAndSecrets()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", TestIterations);
        data.Servers.Add(new ServerProfile
        {
            Name = "web1", Host = "10.0.0.1", Username = "root", Password = "s3cret",
            Tunnels = { new TunnelDefinition { Type = TunnelType.Local, BindPort = 5432, DestinationPort = 5432 } },
        });
        vault.Save(data);
        vault.Lock();

        var reopened = new Vault(VaultPath).Unlock("correct horse");

        var server = Assert.Single(reopened.Servers);
        Assert.Equal("s3cret", server.Password);
        Assert.Equal(5432, Assert.Single(server.Tunnels).BindPort);
    }

    [Fact]
    public void SecretsAreNotStoredInPlaintext()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", TestIterations);
        data.Servers.Add(new ServerProfile { Host = "db.internal", Password = "hunter2-plaintext-check" });
        vault.Save(data);

        var raw = File.ReadAllText(VaultPath);
        Assert.DoesNotContain("hunter2-plaintext-check", raw);
        Assert.DoesNotContain("db.internal", raw);
    }

    [Fact]
    public void WrongPasswordIsRejected()
    {
        new Vault(VaultPath).Create("correct horse", TestIterations);

        var ex = Assert.Throws<VaultException>(() => new Vault(VaultPath).Unlock("wrong password"));
        Assert.Contains("Incorrect", ex.Message);
    }

    [Fact]
    public void TamperedHeaderFailsAuthentication()
    {
        new Vault(VaultPath).Create("correct horse", TestIterations);
        var json = JsonNode.Parse(File.ReadAllText(VaultPath))!;
        json["Salt"] = Convert.ToBase64String(new byte[16]);
        File.WriteAllText(VaultPath, json.ToJsonString());

        Assert.Throws<VaultException>(() => new Vault(VaultPath).Unlock("correct horse"));
    }

    [Fact]
    public void ChangePasswordRequiresCurrentPasswordAndReencrypts()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", TestIterations);

        Assert.Throws<VaultException>(() => vault.ChangePassword("not it", "new password!", data));
        vault.ChangePassword("correct horse", "new password!", data);

        Assert.Throws<VaultException>(() => new Vault(VaultPath).Unlock("correct horse"));
        new Vault(VaultPath).Unlock("new password!");
    }

    [Fact]
    public void ShortPasswordsAreRejected()
    {
        Assert.Throws<VaultException>(() => new Vault(VaultPath).Create("short", TestIterations));
    }

    [Fact]
    public void CannotSaveWhileLocked()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", TestIterations);
        vault.Lock();

        Assert.Throws<VaultException>(() => vault.Save(data));
    }
}
