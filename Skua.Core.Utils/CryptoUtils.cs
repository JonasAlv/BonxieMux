using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Skua.Core.Utils;

public static class CryptoUtils
{
    private const string AppFolderName = "BonxieMux";
    private const string MasterKeyFileName = ".master.key";
    private const int MasterKeyBytes = 32;
    private const int GcmNonceBytes = 12;
    private const int GcmTagBytes = 16;

    private static readonly byte[] LegacySalt = { 0x49, 0x76, 0x61, 0x6e, 0x20, 0x4d, 0x65, 0x64, 0x76, 0x65, 0x64, 0x65, 0x76 };
    private static readonly object KeyLock = new();
    private static byte[]? _cachedMasterKey;

    public static bool IsEncrypted(string? value)
    {
        return !string.IsNullOrEmpty(value) &&
               (value.StartsWith("enc2:", StringComparison.Ordinal) ||
                value.StartsWith("enc:", StringComparison.Ordinal));
    }

    private static byte[] GetOrCreateMasterKey()
    {
        if (_cachedMasterKey is not null)
            return _cachedMasterKey;

        lock (KeyLock)
        {
            if (_cachedMasterKey is not null)
                return _cachedMasterKey;

            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            Directory.CreateDirectory(appDir);
            try
            {
                File.SetUnixFileMode(appDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { }

            string keyPath = Path.Combine(appDir, MasterKeyFileName);
            if (File.Exists(keyPath))
            {
                byte[] existing = File.ReadAllBytes(keyPath);
                if (existing.Length == MasterKeyBytes)
                {
                    try { File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
                    _cachedMasterKey = existing;
                    return existing;
                }
            }

            byte[] generated = RandomNumberGenerator.GetBytes(MasterKeyBytes);
            string tempPath = Path.Combine(appDir, $"{MasterKeyFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (FileStream fs = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    try { File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
                    fs.Write(generated, 0, generated.Length);
                    fs.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(tempPath, keyPath, overwrite: false);
                    _cachedMasterKey = generated;
                    return generated;
                }
                catch (IOException)
                {
                    byte[] winner = File.ReadAllBytes(keyPath);
                    if (winner.Length == MasterKeyBytes)
                    {
                        _cachedMasterKey = winner;
                        return winner;
                    }

                    File.Move(tempPath, keyPath, overwrite: true);
                    _cachedMasterKey = generated;
                    return generated;
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
    }

    private static byte[] GetLegacyKey()
    {
        string machineName = Environment.MachineName ?? "UnknownMachine";
        string userName = Environment.UserName ?? "UnknownUser";
        string passPhrase = $"{machineName}-{userName}-SkuaSecureKey";

        return Rfc2898DeriveBytes.Pbkdf2(passPhrase, LegacySalt, 10000, HashAlgorithmName.SHA256, 32); // 256 bits
    }

    private static byte[] GetLegacyIV()
    {
        return Rfc2898DeriveBytes.Pbkdf2("SkuaStaticIV-DoNotChange", LegacySalt, 10000, HashAlgorithmName.SHA256, 16); // 128 bits
    }

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        if (plainText.StartsWith("enc2:", StringComparison.Ordinal))
            return plainText;

        if (plainText.StartsWith("enc:", StringComparison.Ordinal))
        {
            string decryptedLegacy = Decrypt(plainText);
            if (decryptedLegacy.StartsWith("enc:", StringComparison.Ordinal))
                return plainText;
            plainText = decryptedLegacy;
        }

        try
        {
            byte[] key = GetOrCreateMasterKey();
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] combined = new byte[GcmNonceBytes + GcmTagBytes + plainBytes.Length];

            Span<byte> nonce = combined.AsSpan(0, GcmNonceBytes);
            Span<byte> tag = combined.AsSpan(GcmNonceBytes, GcmTagBytes);
            Span<byte> cipher = combined.AsSpan(GcmNonceBytes + GcmTagBytes, plainBytes.Length);

            RandomNumberGenerator.Fill(nonce);
            using AesGcm aesGcm = new(key, GcmTagBytes);
            aesGcm.Encrypt(nonce, plainBytes, cipher, tag);

            return "enc2:" + Convert.ToBase64String(combined);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CryptoUtils.Encrypt] Failed: {ex.Message}");
            return plainText;
        }
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText;

        if (cipherText.StartsWith("enc2:", StringComparison.Ordinal))
        {
            try
            {
                byte[] combined = Convert.FromBase64String(cipherText.Substring(5));
                if (combined.Length < GcmNonceBytes + GcmTagBytes)
                    return cipherText;

                byte[] key = GetOrCreateMasterKey();
                int cipherLen = combined.Length - GcmNonceBytes - GcmTagBytes;
                byte[] plainBytes = new byte[cipherLen];

                ReadOnlySpan<byte> nonce = combined.AsSpan(0, GcmNonceBytes);
                ReadOnlySpan<byte> tag = combined.AsSpan(GcmNonceBytes, GcmTagBytes);
                ReadOnlySpan<byte> cipher = combined.AsSpan(GcmNonceBytes + GcmTagBytes, cipherLen);

                using AesGcm aesGcm = new(key, GcmTagBytes);
                aesGcm.Decrypt(nonce, cipher, tag, plainBytes);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[CryptoUtils.Decrypt] Failed (enc2): {ex.Message}");
                return cipherText;
            }
        }

        if (!cipherText.StartsWith("enc:", StringComparison.Ordinal))
            return cipherText;

        try
        {
            string actualCipher = cipherText.Substring(4);
            byte[] cipherBytes = Convert.FromBase64String(actualCipher);

            using Aes aes = Aes.Create();
            aes.Key = GetLegacyKey();
            aes.IV = GetLegacyIV();

            ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using MemoryStream ms = new MemoryStream(cipherBytes);
            using CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using StreamReader sr = new StreamReader(cs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CryptoUtils.Decrypt] Failed (legacy): {ex.Message}");
            return cipherText;
        }
    }
}
