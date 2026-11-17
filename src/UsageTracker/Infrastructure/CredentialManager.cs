using System.Runtime.InteropServices;
using System.Text;

namespace UsageTracker.Infrastructure;

/// <summary>Read-only access to generic credentials in Windows Credential Manager.</summary>
internal static class CredentialManager
{
    private const int CredTypeGeneric = 1;

    public static string? ReadGeneric(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(handle);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return DecodeBlob(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <summary>
    /// Git Credential Manager writes UTF-8, but other tools write UTF-16LE. Treat the blob as
    /// UTF-16 when it has an even length and a zero in every odd byte position of its first chars.
    /// </summary>
    internal static string DecodeBlob(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes.Length % 2 == 0)
        {
            var looksUtf16 = true;
            var probe = Math.Min(bytes.Length, 16);
            for (var i = 1; i < probe; i += 2)
            {
                if (bytes[i] != 0) { looksUtf16 = false; break; }
            }
            if (looksUtf16) return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        }
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
