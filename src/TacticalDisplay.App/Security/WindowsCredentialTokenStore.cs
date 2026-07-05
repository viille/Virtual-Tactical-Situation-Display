using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TacticalDisplay.App.Security;

public sealed class WindowsCredentialTokenStore : ISecureTokenStore
{
    private const string TargetName = "VTSD.Cloud.SessionToken";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public Task<string?> GetSessionTokenAsync()
    {
        if (!CredRead(TargetName, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return Task.FromResult<string?>(null);
            throw new Win32Exception(error, "Could not read the VTSD Cloud credential.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var token = credential.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            return Task.FromResult(token);
        }
        finally { CredFree(pointer); }
    }

    public Task SetSessionTokenAsync(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var blob = Marshal.StringToCoTaskMemUni(token);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)(token.Length * 2),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "VTSD Cloud"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the VTSD Cloud credential.");
            return Task.CompletedTask;
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public Task ClearSessionTokenAsync()
    {
        if (!CredDelete(TargetName, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the VTSD Cloud credential.");
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public uint CredentialBlobSize;
        public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes;
        public string? TargetAlias; public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
