using System;
using System.IO;
using System.Linq;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckCloudBackupPortable()
    {
        byte[] source = System.Text.Encoding.UTF8.GetBytes("synthetic EVE refresh token; not a real secret");
        byte[] encrypted = CloudBackupPortableSecurity.Encrypt(source, "correct horse battery staple");
        Check(CloudBackupPortableSecurity.IsEncrypted(encrypted) &&
              !System.Text.Encoding.UTF8.GetString(encrypted).Contains("synthetic EVE"),
              "Portable cloud backup encrypts all contents before transfer");
        Check(CloudBackupPortableSecurity.Decrypt(encrypted, "correct horse battery staple").SequenceEqual(source),
              "Portable cloud backup decrypts with the correct recovery password");
        bool invalidRejected = false;
        try { CloudBackupPortableSecurity.Decrypt(encrypted, "wrong recovery password"); }
        catch (InvalidOperationException) { invalidRejected = true; }
        Check(invalidRejected, "Wrong backup recovery password is rejected before restore");
        byte[] changed = encrypted.ToArray(); changed[^1] ^= 0x44;
        bool damagedRejected = false;
        try { CloudBackupPortableSecurity.Decrypt(changed, "correct horse battery staple"); }
        catch (InvalidOperationException) { damagedRejected = true; }
        Check(damagedRejected, "Corrupted encrypted backup is rejected before restore");
    }
}
