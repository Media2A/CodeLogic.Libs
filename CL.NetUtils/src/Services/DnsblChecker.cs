using System.Net;
using System.Net.Sockets;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;

namespace CL.NetUtils.Services;

/// <summary>
/// Checks IP addresses against DNS-based blacklists (DNSBLs).
/// Supports both IPv4 and IPv6 addresses, primary and fallback service lists.
/// </summary>
public class DnsblChecker
{
    private readonly DnsblConfig _config;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new <see cref="DnsblChecker"/>.
    /// </summary>
    /// <param name="config">DNSBL configuration.</param>
    /// <param name="logger">Optional logger; pass <see langword="null"/> to suppress logging.</param>
    public DnsblChecker(DnsblConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <summary>
    /// Checks whether <paramref name="ipAddress"/> appears on any configured DNSBL,
    /// using the services declared in <see cref="NetUtilsConfig"/>.
    /// Local/private addresses are skipped and returned as <em>not blacklisted</em>.
    /// </summary>
    /// <param name="ipAddress">The IP address string to check (IPv4 or IPv6).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A <see cref="DnsblCheckResult"/> describing the outcome.</returns>
    public Task<DnsblCheckResult> CheckIpAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        var request = new DnsblCheckRequest
        {
            Ipv4Services = _config.Ipv4Services,
            Ipv4FallbackServices = _config.Ipv4FallbackServices,
            Ipv6Services = _config.Ipv6Services,
            Ipv6FallbackServices = _config.Ipv6FallbackServices,
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds),
            DetailedLogging = _config.DetailedLogging
        };
        return CheckIpAsync(ipAddress, request, cancellationToken);
    }

    /// <summary>
    /// Checks <paramref name="ipAddress"/> against the DNSBL zones supplied in
    /// <paramref name="request"/>. Callers that manage their own service lists
    /// and allowlists (e.g. DB-backed admin UIs) use this overload to bypass
    /// the static library configuration.
    /// </summary>
    /// <param name="ipAddress">The IP address string to check (IPv4 or IPv6).</param>
    /// <param name="request">Per-call service lists, allowlist predicate, and timeout.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<DnsblCheckResult> CheckIpAsync(
        string ipAddress,
        DnsblCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                _logger?.Warning($"Invalid IP address: {ipAddress}");
                return DnsblCheckResult.Error(ipAddress, IpAddressType.Unknown, "Invalid IP address format");
            }

            if (IsLocalOrPrivateIp(ip))
            {
                _logger?.Debug($"Skipping DNSBL check for local/private IP: {ipAddress}");
                return DnsblCheckResult.NotBlacklisted(ipAddress, GetAddressType(ip));
            }

            if (request.IsAllowedAsync is not null &&
                await request.IsAllowedAsync(ipAddress, cancellationToken).ConfigureAwait(false))
            {
                _logger?.Debug($"IP {ipAddress} is on the caller-supplied allowlist — skipping DNSBL");
                return DnsblCheckResult.NotBlacklisted(ipAddress, GetAddressType(ip));
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return await CheckIpv4Async(ipAddress, request, cancellationToken).ConfigureAwait(false);

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return await CheckIpv6Async(ipAddress, request, cancellationToken).ConfigureAwait(false);

            _logger?.Debug($"DNSBL check skipped for unsupported address family: {ipAddress}");
            return DnsblCheckResult.NotBlacklisted(ipAddress, IpAddressType.Unknown);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error checking DNSBL for {ipAddress}", ex);
            return DnsblCheckResult.Error(ipAddress, IpAddressType.Unknown, ex.Message);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<DnsblCheckResult> CheckIpv4Async(string ipAddress, DnsblCheckRequest request, CancellationToken cancellationToken)
    {
        var reversedIp = string.Join(".", ipAddress.Split('.').Reverse());
        var timeoutMs = (int)request.Timeout.TotalMilliseconds;

        foreach (var service in request.Ipv4Services)
        {
            var result = await CheckServiceAsync($"{reversedIp}.{service}", ipAddress, service, timeoutMs, request.DetailedLogging, cancellationToken).ConfigureAwait(false);
            if (result.IsBlacklisted)
            {
                _logger?.Warning($"IPv4 {ipAddress} is blacklisted by {service}");
                return result;
            }
        }

        foreach (var service in request.Ipv4FallbackServices)
        {
            var result = await CheckServiceAsync($"{reversedIp}.{service}", ipAddress, service, timeoutMs, request.DetailedLogging, cancellationToken).ConfigureAwait(false);
            if (result.IsBlacklisted)
            {
                _logger?.Warning($"IPv4 {ipAddress} is blacklisted by fallback service {service}");
                return result;
            }
        }

        _logger?.Debug($"IPv4 {ipAddress} is not blacklisted");
        return DnsblCheckResult.NotBlacklisted(ipAddress, IpAddressType.IPv4);
    }

    private async Task<DnsblCheckResult> CheckIpv6Async(string ipAddress, DnsblCheckRequest request, CancellationToken cancellationToken)
    {
        var reversedIpv6 = ReverseIpv6(ipAddress);
        var timeoutMs = (int)request.Timeout.TotalMilliseconds;

        foreach (var service in request.Ipv6Services)
        {
            var result = await CheckServiceAsync($"{reversedIpv6}.{service}", ipAddress, service, timeoutMs, request.DetailedLogging, cancellationToken).ConfigureAwait(false);
            if (result.IsBlacklisted)
            {
                _logger?.Warning($"IPv6 {ipAddress} is blacklisted by {service}");
                return result;
            }
        }

        foreach (var service in request.Ipv6FallbackServices)
        {
            var result = await CheckServiceAsync($"{reversedIpv6}.{service}", ipAddress, service, timeoutMs, request.DetailedLogging, cancellationToken).ConfigureAwait(false);
            if (result.IsBlacklisted)
            {
                _logger?.Warning($"IPv6 {ipAddress} is blacklisted by fallback service {service}");
                return result;
            }
        }

        _logger?.Debug($"IPv6 {ipAddress} is not blacklisted");
        return DnsblCheckResult.NotBlacklisted(ipAddress, IpAddressType.IPv6);
    }

    private async Task<DnsblCheckResult> CheckServiceAsync(
        string lookup,
        string originalIp,
        string service,
        int timeoutMs,
        bool detailedLogging,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            var addresses = await Task.Run(() => Dns.GetHostAddresses(lookup), linked.Token).ConfigureAwait(false);

            if (addresses.Length > 0)
            {
                var addrType = IPAddress.TryParse(originalIp, out var parsed)
                    ? GetAddressType(parsed)
                    : IpAddressType.Unknown;

                return DnsblCheckResult.Blacklisted(originalIp, addrType, service);
            }
        }
        catch (OperationCanceledException)
        {
            if (detailedLogging)
                _logger?.Debug($"DNS lookup timeout for {lookup}");
        }
        catch (Exception ex)
        {
            if (detailedLogging)
                _logger?.Debug($"DNS lookup failed for {lookup}: {ex.Message}");
        }

        var returnType = IPAddress.TryParse(originalIp, out var ip)
            ? GetAddressType(ip)
            : IpAddressType.Unknown;

        return DnsblCheckResult.NotBlacklisted(originalIp, returnType);
    }

    private static bool IsLocalOrPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;

            var b = ip.GetAddressBytes();
            if (b[0] == 0xfc || b[0] == 0xfd) return true;
        }

        return false;
    }

    private static string ReverseIpv6(string ipv6)
    {
        var expanded = IPAddress.Parse(ipv6).GetAddressBytes()
            .SelectMany(b => new[] { (b >> 4) & 0xF, b & 0xF })
            .Reverse();

        return string.Join(".", expanded.Select(n => n.ToString("x")));
    }

    private static IpAddressType GetAddressType(IPAddress ip) =>
        ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => IpAddressType.IPv4,
            AddressFamily.InterNetworkV6 => IpAddressType.IPv6,
            _ => IpAddressType.Unknown
        };
}
