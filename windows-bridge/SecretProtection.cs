using System.Runtime.InteropServices;
using System.Text;

namespace XPlaneEfbBridge;

// Secrets that have to survive a restart (currently the proxy password) are
// encrypted with Windows DPAPI for the current user, so the configuration file
// never contains them in readable form. No extra NuGet dependency: the two
// crypt32 calls are invoked directly.
internal static class SecretProtection
{
    private const string Prefix = "dpapi:";
    private const int CryptProtectUiForbidden = 0x1;

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plain)
    {
        var text = plain ?? "";
        if (text.Length == 0) return "";
        try
        {
            var input = ToBlob(Encoding.UTF8.GetBytes(text));
            try
            {
                if (!CryptProtectData(ref input, "XPlaneEfbBridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                    return "";
                try { return Prefix + Convert.ToBase64String(FromBlob(output)); }
                finally { LocalFree(output.Data); }
            }
            finally { LocalFree(input.Data); }
        }
        catch { return ""; }
    }

    // Returns "" when the value is missing or cannot be decrypted on this account
    // (for example after copying the file to another machine).
    public static string Unprotect(string? stored)
    {
        var text = stored ?? "";
        if (text.Length == 0) return "";
        if (!IsProtected(text)) return text; // value written by an older build, still readable
        try
        {
            var input = ToBlob(Convert.FromBase64String(text[Prefix.Length..]));
            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                    return "";
                try { return Encoding.UTF8.GetString(FromBlob(output)); }
                finally { LocalFree(output.Data); }
            }
            finally { LocalFree(input.Data); }
        }
        catch { return ""; }
    }

    // Never let a credential reach a label, a log or an error message.
    public static string Redact(string? text)
    {
        var value = text ?? "";
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return value;
        var at = value.IndexOf('@', scheme + 3);
        if (at < 0) return value;
        var slash = value.IndexOf('/', scheme + 3);
        if (slash >= 0 && slash < at) return value;
        return $"{value[..(scheme + 3)]}***{value[at..]}";
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DATA_BLOB { Length = data.Length, Data = pointer };
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        var bytes = new byte[blob.Length];
        Marshal.Copy(blob.Data, bytes, 0, blob.Length);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
