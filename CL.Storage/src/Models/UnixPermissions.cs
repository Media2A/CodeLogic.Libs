using System.Globalization;
using System.Text;

namespace CL.Storage.Models;

/// <summary>Converts Unix permission bits between numbers, octal text, and <c>rwxr-xr-x</c> text.</summary>
public static class UnixPermissions
{
    /// <summary>The largest valid mode: all permission bits plus setuid, setgid, and sticky (octal <c>7777</c>).</summary>
    public const int MaxMode = 0xFFF;

    /// <summary>Formats a mode as nine <c>rwx</c> characters, using <c>s</c>/<c>S</c>/<c>t</c>/<c>T</c> for special bits.</summary>
    /// <param name="mode">Permission bits.</param>
    /// <returns>Text such as <c>rwxr-xr-x</c>.</returns>
    public static string Format(int mode)
    {
        var text = new StringBuilder(9);
        Append(text, mode >> 6, (mode & 0x800) != 0, 's');
        Append(text, mode >> 3, (mode & 0x400) != 0, 's');
        Append(text, mode, (mode & 0x200) != 0, 't');
        return text.ToString();
    }

    /// <summary>Parses octal text such as <c>755</c>, <c>0644</c>, or <c>4755</c>.</summary>
    /// <param name="octal">Octal digits, optionally with a leading zero.</param>
    /// <param name="mode">Receives the permission bits.</param>
    /// <returns><see langword="true"/> when the text is a valid mode.</returns>
    public static bool TryParseOctal(string? octal, out int mode)
    {
        mode = 0;
        var text = octal?.Trim() ?? string.Empty;
        if (text.Length is 0 or > 5 || text.Any(c => c is < '0' or > '7'))
            return false;
        mode = Convert.ToInt32(text, 8);
        return mode <= MaxMode;
    }

    /// <summary>Formats a mode as four octal digits, such as <c>0755</c>.</summary>
    /// <param name="mode">Permission bits.</param>
    /// <returns>Octal text.</returns>
    public static string ToOctal(int mode) => Convert.ToString(mode, 8).PadLeft(4, '0');

    /// <summary>
    /// Returns the mode as the decimal number whose digits are its octal digits (0755 becomes 755),
    /// the form FTP <c>SITE CHMOD</c> helpers and SSH.NET expect.
    /// </summary>
    internal static int ToOctalDigits(int mode) => int.Parse(Convert.ToString(mode, 8), CultureInfo.InvariantCulture);

    private static void Append(StringBuilder text, int bits, bool special, char specialChar)
    {
        text.Append((bits & 4) != 0 ? 'r' : '-');
        text.Append((bits & 2) != 0 ? 'w' : '-');
        var execute = (bits & 1) != 0;
        text.Append(special ? (execute ? specialChar : char.ToUpperInvariant(specialChar)) : execute ? 'x' : '-');
    }
}
