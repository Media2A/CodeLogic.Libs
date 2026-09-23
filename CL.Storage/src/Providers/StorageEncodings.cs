using System.Text;

namespace CL.Storage.Providers;

/// <summary>
/// Resolves file-name encodings, including legacy code pages (windows-1252, iso-8859-x, cp437,
/// shift_jis) that older FTP and SFTP servers use and .NET does not enable by default.
/// </summary>
internal static class StorageEncodings
{
    static StorageEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Get(string name) => Encoding.GetEncoding(name);

    public static bool IsKnown(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try { _ = Get(name); return true; }
        catch (ArgumentException) { return false; }
    }
}
