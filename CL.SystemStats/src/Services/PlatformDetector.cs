using CL.SystemStats.Abstractions;

namespace CL.SystemStats.Services;

/// <summary>
/// Default implementation of <see cref="IPlatformDetector"/>.
/// Uses <see cref="OperatingSystem"/> methods to identify the current platform at startup.
/// </summary>
public sealed class PlatformDetector : IPlatformDetector
{
    /// <inheritdoc/>
    public PlatformType Platform { get; }

    /// <inheritdoc/>
    public bool IsWindows => Platform == PlatformType.Windows;

    /// <inheritdoc/>
    public bool IsLinux => Platform == PlatformType.Linux;

    /// <inheritdoc/>
    public bool IsMacOS => Platform == PlatformType.MacOS;

    /// <summary>
    /// Initializes a new <see cref="PlatformDetector"/> by probing the running OS.
    /// </summary>
    public PlatformDetector()
    {
        Platform = OperatingSystem.IsWindows() ? PlatformType.Windows
                 : OperatingSystem.IsLinux()   ? PlatformType.Linux
                 : OperatingSystem.IsMacOS()   ? PlatformType.MacOS
                 : PlatformType.Unknown;
    }
}
