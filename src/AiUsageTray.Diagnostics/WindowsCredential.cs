using System.Runtime.InteropServices;
using System.Text;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public static class WindowsCredential
{
    public static string Read(string target)
    {
        if (!OperatingSystem.IsWindows())
            throw new UsageException(FailureKind.MissingCredential, "windows_credential_required");
        if (!CredRead(target, 1, 0, out var pointer))
            throw new UsageException(FailureKind.MissingCredential, "selected_credential_not_found");
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > 16384)
                throw new UsageException(FailureKind.MissingCredential, "selected_credential_invalid");
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                // gh stores UTF-8; some Windows credential writers use UTF-16LE.
                return (bytes.Length > 1 && bytes[1] == 0 ? Encoding.Unicode : Encoding.UTF8)
                    .GetString(bytes).TrimEnd('\0');
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
