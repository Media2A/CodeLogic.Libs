using System.Collections.Concurrent;
using MySqlConnector;

namespace CL.MySQL2.Core;

/// <summary>
/// Builds and caches compiled row-materializer delegates for entity type <typeparamref name="T"/>.
/// <para>
/// Column order in the incoming reader is not fixed (depends on SELECT list, projections, joins),
/// so materializers are keyed by a reader-shape fingerprint: the ordered tuple of
/// (columnName) pairs exposed by the reader. A given fingerprint is hit ≥99% of the time after
/// first compile for any stable query.
/// </para>
/// </summary>
internal sealed class Materializer<T> where T : class
{
    private readonly ConcurrentDictionary<string, Func<MySqlDataReader, T>> _byShape = new();

    /// <summary>
    /// Returns a compiled materializer for the reader's current shape (set of columns and
    /// their ordinals). The first call for a given shape incurs a one-time IL-gen cost;
    /// subsequent calls for the same shape are cache hits.
    /// </summary>
    public Func<MySqlDataReader, T> CompileForReader(MySqlDataReader reader)
    {
        var shape = BuildShapeKey(reader);
        return _byShape.GetOrAdd(shape, _ => BuildMaterializer(reader));
    }

    private static string BuildShapeKey(MySqlDataReader reader)
    {
        var n = reader.FieldCount;
        var parts = new string[n];
        for (var i = 0; i < n; i++) parts[i] = reader.GetName(i);
        return string.Join("\u0001", parts);
    }

    private static Func<MySqlDataReader, T> BuildMaterializer(MySqlDataReader reader)
    {
        // Resolve column ordinals up front so the delegate body is pure index-based reads.
        var fieldCount = reader.FieldCount;
        var fieldToColumn = new ColumnMetadata?[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            var name = reader.GetName(i);
            fieldToColumn[i] = EntityMetadata<T>.TryResolve(name);
        }

        // Capture the array in a closure — each index lookup in the delegate is O(1)
        // and no per-row reflection occurs.
        return rdr =>
        {
            var entity = Activator.CreateInstance<T>();
            for (var i = 0; i < fieldCount; i++)
            {
                var col = fieldToColumn[i];
                if (col is null || !col.Property.CanWrite) continue;
                var raw = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                var converted = TypeConverter.FromDbValue(raw, col.Property.PropertyType);
                col.Set(entity, converted);
            }
            return entity;
        };
    }
}
