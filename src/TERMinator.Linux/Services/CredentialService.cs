using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TERMinator.Linux.Services;

/// <summary>
/// Stores credentials securely using AES encryption with a machine-specific key.
/// On Linux, derives the encryption key from /etc/machine-id (or falls back to
/// a generated key stored alongside the credentials).
/// Migrates existing plaintext credentials.json automatically.
/// </summary>
public class CredentialService
{
    private readonly string _filePath;
    private readonly string _legacyFilePath;

    public CredentialService()
    {
        var appDir = Models.AppSettings.GetAppDataDir();
        _filePath = Path.Combine(appDir, "credentials.dat");
        _legacyFilePath = Path.Combine(appDir, "credentials.json");
        MigrateLegacy();
    }

    public void SavePassword(string connectionId, string password)
    {
        var credentials = LoadAll();
        credentials[connectionId] = password;
        SaveAll(credentials);
    }

    public string? GetPassword(string connectionId)
    {
        var credentials = LoadAll();
        return credentials.TryGetValue(connectionId, out var password) ? password : null;
    }

    public void DeletePassword(string connectionId)
    {
        var credentials = LoadAll();
        if (credentials.Remove(connectionId))
            SaveAll(credentials);
    }

    private Dictionary<string, string> LoadAll()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>();
        try
        {
            var fileBytes = File.ReadAllBytes(_filePath);
            var decrypted = Decrypt(fileBytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(decrypted)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private void SaveAll(Dictionary<string, string> credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        var encrypted = Encrypt(Encoding.UTF8.GetBytes(json));
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(_filePath, encrypted);
    }

    /// <summary>
    /// Migrate old plaintext credentials.json to encrypted credentials.dat.
    /// </summary>
    private void MigrateLegacy()
    {
        if (!File.Exists(_legacyFilePath) || File.Exists(_filePath))
            return;
        try
        {
            var json = File.ReadAllText(_legacyFilePath);
            var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (creds != null && creds.Count > 0)
            {
                SaveAll(creds);
                File.Delete(_legacyFilePath);
            }
        }
        catch
        {
            // If migration fails, leave legacy file in place
        }
    }

    private static byte[] DeriveKey()
    {
        // Try /etc/machine-id first (available on most Linux distros)
        string? machineId = null;
        try
        {
            if (File.Exists("/etc/machine-id"))
                machineId = File.ReadAllText("/etc/machine-id").Trim();
            else if (File.Exists("/var/lib/dbus/machine-id"))
                machineId = File.ReadAllText("/var/lib/dbus/machine-id").Trim();
        }
        catch { /* ignore */ }

        // Fall back to a stable user-specific seed
        if (string.IsNullOrEmpty(machineId))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            machineId = $"TERMinator-{home}-fallback";
        }

        // Derive a 256-bit key using PBKDF2
        var salt = "TERMinator-Linux-Cred-Salt-v1"u8.ToArray();
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(machineId),
            salt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    private static byte[] Encrypt(byte[] plaintext)
    {
        var key = DeriveKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var ms = new MemoryStream();
        // Write IV first (16 bytes)
        ms.Write(aes.IV, 0, aes.IV.Length);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            cs.Write(plaintext, 0, plaintext.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Decrypt(byte[] ciphertext)
    {
        if (ciphertext.Length < 17) // IV (16) + at least 1 byte
            throw new CryptographicException("Invalid ciphertext");

        var key = DeriveKey();
        using var aes = Aes.Create();
        aes.Key = key;

        var iv = new byte[16];
        Array.Copy(ciphertext, 0, iv, 0, 16);
        aes.IV = iv;

        using var ms = new MemoryStream(ciphertext, 16, ciphertext.Length - 16);
        using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var result = new MemoryStream();
        cs.CopyTo(result);
        return result.ToArray();
    }
}
