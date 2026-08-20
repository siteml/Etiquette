using System.Runtime.InteropServices;
using System.Text;

namespace Etiq.Core;

/// <summary>
/// Machine-bound secret protection via Windows DPAPI (CryptProtectData),
/// P/Invoked directly so no NuGet package is needed. C# port of
/// reference/labelprint/dpapi_windows.go + secure.go semantics:
///
/// - Encrypted values carry the "dpapi:" prefix (base64 payload).
/// - Plaintext placeholders ("PASTE-...", "EPICOR-...") are left alone.
/// - Each secret is handled independently, so a freshly pasted plaintext
///   apiKey next to an already-encrypted password re-encrypts only the new one.
///
/// LocalMachine scope (any user on the station can print), entropy pins the
/// blob to this application.
/// </summary>
public static class CredentialStore
{
    public const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("etiquette-labelprint-v1");

    /// <summary>True when the value is a real secret needing encryption.</summary>
    public static bool SecretPresent(string? v) =>
        !string.IsNullOrEmpty(v) && !v.StartsWith(Prefix) &&
        !v.StartsWith("PASTE-") && !v.StartsWith("EPICOR-");

    public static string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential protection requires Windows");
        var blob = ProtectRaw(Encoding.UTF8.GetBytes(plaintext));
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Returns the decrypted value for "dpapi:" strings; passes anything else through.</summary>
    public static string Unprotect(string value)
    {
        if (!value.StartsWith(Prefix)) return value;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential protection requires Windows");
        var blob = Convert.FromBase64String(value[Prefix.Length..]);
        return Encoding.UTF8.GetString(UnprotectRaw(blob));
    }

    // ---------- P/Invoke ----------

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
        uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
        uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] ProtectRaw(byte[] data) => Dpapi(data, protect: true);
    private static byte[] UnprotectRaw(byte[] data) => Dpapi(data, protect: false);

    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var input = Alloc(data);
        var entropy = Alloc(Entropy);
        try
        {
            uint flags = CRYPTPROTECT_UI_FORBIDDEN | (protect ? CRYPTPROTECT_LOCAL_MACHINE : 0);
            bool ok = protect
                ? CryptProtectData(ref input, "etiquette", ref entropy, IntPtr.Zero, IntPtr.Zero, flags, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, flags, out output);
            if (!ok)
                throw new InvalidOperationException(
                    (protect ? "CryptProtectData" : "CryptUnprotectData") +
                    $" failed (win32 error {Marshal.GetLastWin32Error()})");
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            Marshal.FreeHGlobal(entropy.pbData);
        }
    }

    private static DATA_BLOB Alloc(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }
}
