using System.Text.Json.Nodes;
using Ussh.Core.Models;
using Ussh.Core.Security;

namespace Ussh.Core.Tests;

public sealed class VaultTests : IDisposable
{
    // Low iteration count keeps the tests fast; production uses Vault.DefaultIterations.
    private const int TestIterations = 100_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zssh-tests-" + Guid.NewGuid().ToString("N"));
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
    public void ForgottenVaultIsSetAsideNotDeleted()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("forgotten pass", TestIterations);
        data.Servers.Add(new ServerProfile { Host = "old.example", Password = "old-secret" });
        vault.Save(data);
        vault.Save(data); // also produces vault.json.bak
        vault.Lock();

        var aside = new Vault(VaultPath).SetAsideForgotten();

        Assert.False(File.Exists(VaultPath));
        Assert.False(File.Exists(VaultPath + ".bak"));
        Assert.True(File.Exists(aside));
        Assert.True(File.Exists(aside + ".bak"));
        Assert.Matches(@"vault\.forgotten-\d{8}-\d{6}(-\d+)?\.json$", aside);

        // A fresh vault can be created with a new password…
        var fresh = new Vault(VaultPath).Create("a new password", TestIterations);
        Assert.Empty(fresh.Servers);
        // …and the old one is intact: it opens with the old password if it's remembered.
        Assert.Equal("old-secret", Assert.Single(new Vault(aside).Unlock("forgotten pass").Servers).Password);
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

    [Fact]
    public void DataFromBeforeTheRenameIsMovedAndStillUnlocks()
    {
        var oldDir = Path.Combine(_dir, "ussh");
        var newDir = Path.Combine(_dir, "zssh");
        new Vault(Path.Combine(oldDir, "vault.json")).Create("correct horse battery", TestIterations);

        Assert.Equal(newDir, AppPaths.MigrateFromOldName(oldDir, newDir));

        Assert.False(Directory.Exists(oldDir));
        new Vault(Path.Combine(newDir, "vault.json")).Unlock("correct horse battery");
        // Once moved (or on a fresh install), nothing more happens.
        Assert.Equal(newDir, AppPaths.MigrateFromOldName(oldDir, newDir));
    }
}
