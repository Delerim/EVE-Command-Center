using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace EveMultiPreview.Services;

internal static class EveCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const string Prefix = "EVECommandCenter:SSO:";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target, uint type, uint flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static string Target(long characterId) => Prefix + characterId;

    public static void Write(long characterId, string refreshToken)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(refreshToken);
        IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Target(characterId),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = characterId.ToString()
            };

            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not save the EVE SSO refresh token.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static string? Read(long characterId)
    {
        if (!CredRead(Target(characterId), CredTypeGeneric, 0, out IntPtr ptr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize == 0)
                return null;

            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(long characterId)
    {
        if (!CredDelete(Target(characterId), CredTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168) // ERROR_NOT_FOUND
                throw new Win32Exception(error);
        }
    }
}
