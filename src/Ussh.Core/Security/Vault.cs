using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ussh.Core.Models;

namespace Ussh.Core.Security;

public sealed class VaultException : Exception
{
    public VaultException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Encrypted store for server profiles and settings, protected by the admin (master) password.
///
/// Format: a JSON envelope holding PBKDF2-SHA256 parameters and an AES-256-GCM ciphertext of
/// the serialized <see cref="VaultData"/>. The envelope header is bound in as associated data,
/// so tampering with the KDF parameters fails authentication rather than silently weakening it.
/// The same file works unchanged on Windows and Linux.
/// </summary>
public sealed class Vault
{
    public const int DefaultIterations = 600_000;
    public const int MinimumPasswordLength = 8;
    private const string FormatName = "ussh-vault";
    private const int FormatVersion = 1;
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private byte[]? _key;
    private byte[]? _salt;
    private int _iterations;

    public Vault(string path) => _path = path;

    public string Path => _path;
    public bool Exists => File.Exists(_path);
    public bool IsUnlocked { get { lock (_gate) return _key != null; } }

    /// <summary>Creates a new, empty vault. Fails if one already exists.</summary>
    public VaultData Create(string password, int iterations = DefaultIterations)
    {
        ValidateNewPassword(password);
        lock (_gate)
        {
            if (Exists)
                throw new VaultException("A vault already exists.");
            _salt = RandomNumberGenerator.GetBytes(SaltSize);
            _iterations = iterations;
            _key = DeriveKey(password, _salt, _iterations);
            var data = new VaultData();
            SaveLocked(data);
            return data;
        }
    }

    /// <summary>Decrypts the vault. Throws <see cref="VaultException"/> on a wrong password or a damaged file.</summary>
    public VaultData Unlock(string password)
    {
        lock (_gate)
        {
            var envelope = ReadEnvelope();
            var salt = Convert.FromBase64String(envelope.Salt);
            var key = DeriveKey(password, salt, envelope.Iterations);
            try
            {
                var data = Decrypt(envelope, key);
                _key = key;
                _salt = salt;
                _iterations = envelope.Iterations;
                return data;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
    }

    public void Save(VaultData data)
    {
        lock (_gate)
        {
            if (_key == null)
                throw new VaultException("The vault is locked.");
            SaveLocked(data);
        }
    }

    /// <summary>Re-encrypts the vault under a new password. Requires the current password.</summary>
    public void ChangePassword(string currentPassword, string newPassword, VaultData data)
    {
        ValidateNewPassword(newPassword);
        lock (_gate)
        {
            // Proves knowledge of the current password even though the vault is already unlocked.
            Unlock(currentPassword);
            Lock();
            _salt = RandomNumberGenerator.GetBytes(SaltSize);
            _iterations = Math.Max(_iterations, DefaultIterations);
            _key = DeriveKey(newPassword, _salt, _iterations);
            SaveLocked(data);
        }
    }

    public void Lock()
    {
        lock (_gate)
        {
            if (_key != null)
                CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }

    /// <summary>
    /// For a forgotten admin password: sets the current vault aside so a new one can be created.
    /// Nothing is deleted. The vault (and its backup) are renamed to
    /// "vault.forgotten-yyyyMMdd-HHmmss.json" in the same folder: still encrypted, and usable again
    /// (renamed back) if the password turns up. Returns the new name of the set-aside vault.
    /// </summary>
    public string SetAsideForgotten()
    {
        lock (_gate)
        {
            if (!Exists)
                throw new VaultException("No vault exists yet.");
            Lock();
            var directory = System.IO.Path.GetDirectoryName(_path) ?? ".";
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var aside = System.IO.Path.Combine(directory, $"vault.forgotten-{stamp}.json");
            for (var i = 2; File.Exists(aside); i++)
                aside = System.IO.Path.Combine(directory, $"vault.forgotten-{stamp}-{i}.json");
            File.Move(_path, aside);
            if (File.Exists(_path + ".bak"))
                File.Move(_path + ".bak", aside + ".bak");
            return aside;
        }
    }

    public static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            throw new VaultException($"The admin password must be at least {MinimumPasswordLength} characters.");
    }

    private void SaveLocked(VaultData data)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        try
        {
            var envelope = new Envelope
            {
                Format = FormatName,
                Version = FormatVersion,
                Kdf = "pbkdf2-sha256",
                Iterations = _iterations,
                Salt = Convert.ToBase64String(_salt!),
            };
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(_key!, TagSize))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, envelope.AssociatedData());
            envelope.Nonce = Convert.ToBase64String(nonce);
            envelope.Tag = Convert.ToBase64String(tag);
            envelope.Ciphertext = Convert.ToBase64String(ciphertext);
            WriteAtomically(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void WriteAtomically(byte[] bytes)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(_path))
            File.Copy(_path, _path + ".bak", overwrite: true);
        File.Move(tmp, _path, overwrite: true);
        RestrictPermissions(_path);
    }

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (File.Exists(path + ".bak"))
                    File.SetUnixFileMode(path + ".bak", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private Envelope ReadEnvelope()
    {
        if (!Exists)
            throw new VaultException("No vault exists yet.");
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(_path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new VaultException("The vault file is damaged.", ex);
        }
        if (envelope == null || envelope.Format != FormatName)
            throw new VaultException("The file is not a zSSH vault.");
        if (envelope.Version != FormatVersion)
            throw new VaultException($"Unsupported vault version {envelope.Version}.");
        if (envelope.Kdf != "pbkdf2-sha256" || envelope.Iterations < 100_000)
            throw new VaultException("The vault uses unsupported key derivation settings.");
        return envelope;
    }

    private static VaultData Decrypt(Envelope envelope, byte[] key)
    {
        byte[]? plaintext = null;
        try
        {
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, envelope.AssociatedData());
            return JsonSerializer.Deserialize<VaultData>(plaintext, JsonOptions) ?? new VaultData();
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new VaultException("Incorrect admin password.", ex);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            throw new VaultException("The vault file is damaged.", ex);
        }
        finally
        {
            if (plaintext != null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string Kdf { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Ciphertext { get; set; } = "";

        public byte[] AssociatedData() => Encoding.UTF8.GetBytes($"{Format}|{Version}|{Kdf}|{Iterations}|{Salt}");
    }
}
