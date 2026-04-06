namespace CL.SystemStats.Abstractions;

/// <summary>
/// Represents the detected operating system platform.
/// </summary>
public enum PlatformType
{
    /// <summary>Microsoft Windows.</summary>
    Windows,
    /// <summary>Linux (including embedded).</summary>
    Linux,
    /// <summary>macOS / OS X.</summary>
    MacOS,
    /// <summary>Platform could not be determined.</summary>
    Unknown
}

/// <summary>
/// Detects the current operating system platform.
/// </summary>
public interface IPlatformDetector
{
    /// <summary>
    /// Gets the detected platform type.
    /// </summary>
    PlatformType Platform { get; }

    /// <summary>
    /// Returns <see langword="true"/> when the current platform is Windows.
    /// </summary>
    bool IsWindows { get; }

    /// <summary>
    /// Returns <see langword="true"/> when the current platform is Linux.
    /// </summary>
    bool IsLinux { get; }

    /// <summary>
    /// Returns <see langword="true"/> when the current platform is macOS.
    /// </summary>
    bool IsMacOS { get; }
}
