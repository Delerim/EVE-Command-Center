using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EveCommandCenter.Services;

// The private app-data backup is portable and encrypted independently of Google.
// The recovery password is protected by Windows DPAPI locally, not backed up.
public static class CloudBackupPortableSecurity
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ECCENC02");
    private const int Iterations = 600_000;
    private static string PasswordFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EVE Command Center", "CloudBackup", "recovery-key.dat");

    public static bool IsConfigured => File.Exists(PasswordFile);

    public static void RememberPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new InvalidOperationException("Choose a recovery password of at least 12 characters. Keep it outside this PC: another PC will need it to restore your EVE tokens.");
        Directory.CreateDirectory(Path.GetDirectoryName(PasswordFile)!);
        byte[] input = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(input, null, DataProtectionScope.CurrentUser);
            string temporary = PasswordFile + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, PasswordFile, true);
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public static string ReadPassword()
    {
        if (!File.Exists(PasswordFile))
            throw new InvalidOperationException("Set your encrypted-backup recovery password first. EVE tokens cannot be uploaded in an unencrypted archive.");
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(PasswordFile), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static bool IsEncrypted(ReadOnlySpan<byte> data) =>
        data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encrypt(byte[] plaintext, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);
            byte[] result = new byte[Magic.Length + 16 + 12 + 16 + ciphertext.Length];
            int cursor = 0;
            Magic.CopyTo(result.AsSpan(cursor)); cursor += Magic.Length;
            salt.CopyTo(result.AsSpan(cursor)); cursor += salt.Length;
            nonce.CopyTo(result.AsSpan(cursor)); cursor += nonce.Length;
            tag.CopyTo(result.AsSpan(cursor)); cursor += tag.Length;
            ciphertext.CopyTo(result.AsSpan(cursor));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Decrypt(byte[] data, string password)
    {
        if (!IsEncrypted(data) || data.Length < Magic.Length + 44)
            throw new InvalidDataException("This is not an EVE Command Center encrypted backup.");
        int offset = Magic.Length;
        byte[] salt = data.AsSpan(offset, 16).ToArray(); offset += 16;
        byte[] nonce = data.AsSpan(offset, 12).ToArray(); offset += 12;
        byte[] tag = data.AsSpan(offset, 16).ToArray(); offset += 16;
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            byte[] plain = new byte[data.Length - offset];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, data.AsSpan(offset), tag, plain, Magic);
            return plain;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Recovery password incorrect or backup was altered. Nothing has been restored.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
