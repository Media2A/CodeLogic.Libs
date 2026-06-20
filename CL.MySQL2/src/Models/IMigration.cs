using CL.MySQL2.Services;

namespace CL.MySQL2.Models;

/// <summary>
/// Identifies a migration's position in the ordered sequence: the application version it ships
/// with, then an explicit order within that version. Sorts by semantic version, then order.
/// </summary>
public readonly record struct MigrationVersion(string AppVersion, int Order)
    : IComparable<MigrationVersion>
{
    /// <inheritdoc/>
    public int CompareTo(MigrationVersion other)
    {
        var v = CompareSemVer(AppVersion, other.AppVersion);
        return v != 0 ? v : Order.CompareTo(other.Order);
    }

    /// <summary>True when this version is at or below <paramref name="appVersion"/> (semver compare).</summary>
    public bool IsAtOrBelow(string appVersion) => CompareSemVer(AppVersion, appVersion) <= 0;

    /// <inheritdoc/>
    public override string ToString() => $"{AppVersion}/{Order:D3}";

    /// <summary>
    /// Compares two dotted version strings numerically (major.minor.patch.…). Non-numeric or
    /// missing components are treated as 0, so "1.2" == "1.2.0". Pre-release tags are ignored.
    /// </summary>
    internal static int CompareSemVer(string a, string b)
    {
        static int[] Parse(string s)
        {
            var core = (s ?? "0").Split('-', '+')[0];
            return core.Split('.')
                .Select(p => int.TryParse(p, out var n) ? n : 0)
                .ToArray();
        }

        var pa = Parse(a);
        var pb = Parse(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}

/// <summary>
/// An explicit, ordered, imperative migration — for data transforms, seeds, and semantic schema
/// changes that the declarative CRC-gated sync cannot express. Discovered by registration or
/// assembly scan and run by the <see cref="MigrationRunner"/> in <see cref="Version"/> order.
/// </summary>
public interface IMigration
{
    /// <summary>The migration's ordered position.</summary>
    MigrationVersion Version { get; }

    /// <summary>Human-readable description recorded in the <c>__migrations</c> history.</summary>
    string Description { get; }

    /// <summary>Applies the migration. Runs inside a transaction owned by the runner.</summary>
    Task UpAsync(IMigrationContext ctx, CancellationToken ct);

    /// <summary>
    /// Reverts the migration. Override to enable rollback; the default
    /// <see cref="Migration"/> base throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task DownAsync(IMigrationContext ctx, CancellationToken ct);
}

/// <summary>
/// Convenience base for <see cref="IMigration"/>. Supplies <see cref="Version"/> and
/// <see cref="Description"/> from the constructor and a <see cref="DownAsync"/> that throws
/// (override it to make the migration reversible).
/// </summary>
/// <remarks>
/// Note that MySQL implicitly commits on DDL. A migration that mixes <c>ALTER</c> with data
/// changes is therefore not atomic — prefer keeping <see cref="UpAsync"/> steps idempotent and
/// splitting heavy DDL and heavy backfill into separate migrations.
/// </remarks>
public abstract class Migration : IMigration
{
    /// <param name="appVersion">The app version this migration ships with (e.g. "1.4.0").</param>
    /// <param name="order">Order within that version (applied ascending).</param>
    /// <param name="description">Human-readable description.</param>
    protected Migration(string appVersion, int order, string description)
    {
        Version = new MigrationVersion(appVersion, order);
        Description = description;
    }

    /// <inheritdoc/>
    public MigrationVersion Version { get; }

    /// <inheritdoc/>
    public string Description { get; }

    /// <inheritdoc/>
    public abstract Task UpAsync(IMigrationContext ctx, CancellationToken ct);

    /// <inheritdoc/>
    public virtual Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Migration '{GetType().Name}' ({Version}) does not support rollback. Override DownAsync to enable it.");
}
