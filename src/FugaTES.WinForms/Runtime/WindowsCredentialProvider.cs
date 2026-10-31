using System.Runtime.InteropServices;

namespace FugaTES.WinForms.Runtime;

public static class WindowsCredentialProvider
{
    public static string ReadPassword(string target, string expectedUsername)
    {
        if (!CredRead(target, 1, 0, out var pointer))
            throw new DesktopStartupException("DESKTOP_DB_CREDENTIAL_MISSING");
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.Username != expectedUsername || credential.BlobSize == 0 || credential.BlobSize % 2 != 0)
                throw new DesktopStartupException("DESKTOP_DB_CREDENTIAL_INVALID");
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2))
                ?? throw new DesktopStartupException("DESKTOP_DB_CREDENTIAL_INVALID");
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string Alias, Username;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}