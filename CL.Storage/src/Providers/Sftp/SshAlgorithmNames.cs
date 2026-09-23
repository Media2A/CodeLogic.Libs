using Renci.SshNet;

namespace CL.Storage.Providers.Sftp;

/// <summary>Validates and applies SSH algorithm allow-lists against what the SSH library implements.</summary>
internal static class SshAlgorithmNames
{
    private static readonly Lazy<Supported> Known = new(() =>
    {
        var probe = new ConnectionInfo("localhost", "probe", new NoneAuthenticationMethod("probe"));
        return new Supported(
            [.. probe.KeyExchangeAlgorithms.Keys],
            [.. probe.Encryptions.Keys],
            [.. probe.HmacAlgorithms.Keys],
            [.. probe.HostKeyAlgorithms.Keys]);
    });

    public static IEnumerable<string> GetValidationErrors(
        List<string>? keyExchange,
        List<string>? ciphers,
        List<string>? macs,
        List<string>? hostKeys)
    {
        var known = Known.Value;
        foreach (var error in Check("KeyExchangeAlgorithms", keyExchange, known.KeyExchange)) yield return error;
        foreach (var error in Check("Ciphers", ciphers, known.Ciphers)) yield return error;
        foreach (var error in Check("MacAlgorithms", macs, known.Macs)) yield return error;
        foreach (var error in Check("HostKeyAlgorithms", hostKeys, known.HostKeys)) yield return error;
    }

    /// <summary>Keeps only the listed algorithms, in the listed order, so the list is also the preference.</summary>
    public static void Restrict<T>(IDictionary<string, T> available, IReadOnlyList<string>? allowed)
    {
        if (allowed is null || allowed.Count == 0)
            return;
        var kept = allowed.Where(available.ContainsKey).Select(name => (name, available[name])).ToArray();
        available.Clear();
        foreach (var (name, value) in kept)
            available[name] = value;
    }

    private static IEnumerable<string> Check(string setting, List<string>? requested, HashSet<string> known)
    {
        foreach (var name in requested ?? [])
        {
            if (!known.Contains(name))
                yield return $"{setting} contains '{name}', which is not supported. Supported: {string.Join(", ", known.Order(StringComparer.Ordinal))}";
        }
    }

    private sealed record Supported(HashSet<string> KeyExchange, HashSet<string> Ciphers, HashSet<string> Macs, HashSet<string> HostKeys);
}
