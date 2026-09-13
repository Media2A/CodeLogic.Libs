using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using CL.PostgreSQL.Models;
using Npgsql;

namespace CL.PostgreSQL.Core;

/// <summary>
/// Single source of truth for reflected metadata about entity type <typeparamref name="T"/>.
/// Populated once per type on first access and held for the lifetime of the AppDomain.
/// Replaces the per-file caches that previously lived in <c>Repository</c>,
/// <c>QueryBuilder</c>, and <c>SchemaAnalyzer</c>.
/// </summary>
internal static class EntityMetadata<T> where T : class
{
    public static readonly string TableName;

    /// <summary>
    /// The schema declared by <see cref="TableAttribute.Schema"/>, or null when the entity
    /// does not name one — in which case it lands in the connection's configured
    /// <c>DefaultSchema</c>. An explicit attribute always wins.
    /// </summary>
    public static readonly string? ExplicitSchemaName;

    /// <summary>
    /// The schema the entity lives in on the <c>Default</c> connection:
    /// <see cref="TableAttribute.Schema"/> when set, else that connection's configured
    /// <c>DefaultSchema</c> (<c>public</c> unless changed). Prefer
    /// <see cref="SchemaNameFor"/> wherever the connection ID is known — two named
    /// connections may configure different schemas.
    /// </summary>
    public static string SchemaName => SchemaNameFor(null);

    /// <summary>
    /// The quoted, schema-qualified table reference on the <c>Default</c> connection, e.g.
    /// <c>"public"."users"</c>. Prefer <see cref="QualifiedTableNameFor"/>.
    /// </summary>
    public static string QualifiedTableName => QualifiedTableNameFor(null);

    // Per-connection qualified names. Stamped with the schema-cache generation so a
    // configuration change (or a stop/start inside one test host) is picked up rather than
    // serving a name built against the previous default schema.
    private static readonly ConcurrentDictionary<string, (int Generation, string Schema, string Qualified)> _perConnection =
        new(StringComparer.OrdinalIgnoreCase);

    // Stands in for a null connection ID. Not a legal connection ID itself, so it cannot
    // collide with a real one.
    private const string NullConnectionKey = "<default connection>";

    /// <summary>
    /// The schema this entity maps to on <paramref name="connectionId"/>:
    /// <see cref="TableAttribute.Schema"/> when the entity declares one, otherwise that
    /// connection's configured <c>DefaultSchema</c>.
    /// </summary>
    public static string SchemaNameFor(string? connectionId) => Resolve(connectionId).Schema;

    /// <summary>The quoted, schema-qualified table reference for <paramref name="connectionId"/>.</summary>
    public static string QualifiedTableNameFor(string? connectionId) => Resolve(connectionId).Qualified;

    private static (string Schema, string Qualified) Resolve(string? connectionId)
    {
        var key = connectionId ?? NullConnectionKey;
        var generation = EntityMetadataSchemaCache.Generation;

        if (_perConnection.TryGetValue(key, out var cached) && cached.Generation == generation)
            return (cached.Schema, cached.Qualified);

        var schema = ExplicitSchemaName
                     ?? RequireSafeIdentifier(
                         PostgreSqlRuntimeOptions.DefaultSchemaFor(connectionId), typeof(T), "schema");
        var qualified = PostgreSqlDialect.Qualify(schema, TableName);
        _perConnection[key] = (generation, schema, qualified);
        return (schema, qualified);
    }

    public static readonly TableAttribute? TableAttr;
    public static readonly IReadOnlyList<ColumnMetadata> Columns;
    public static readonly IReadOnlyDictionary<string, ColumnMetadata> ColumnsByColumnName;
    public static readonly IReadOnlyDictionary<string, ColumnMetadata> ColumnsByPropertyName;
    public static readonly ColumnMetadata? PrimaryKey;
    public static readonly IReadOnlyList<CompositeIndexAttribute> CompositeIndexes;
    public static readonly IReadOnlySet<string> AllColumnNames;

    /// <summary>
    /// The soft-delete timestamp column when the entity carries <see cref="SoftDeleteAttribute"/>,
    /// else null. Reads filter on <c>{column} IS NULL</c> and deletes set it to UtcNow.
    /// </summary>
    public static readonly ColumnMetadata? SoftDeleteColumn;

    /// <summary>
    /// Compiled row materializer: reads a <see cref="NpgsqlDataReader"/> positioned on a row
    /// and returns a fully hydrated <typeparamref name="T"/>. Uses column ordinals resolved
    /// once per query (see <see cref="Materializer{T}.CompileForReader"/>).
    /// </summary>
    public static readonly Materializer<T> Materializer;

    static EntityMetadata()
    {
        var type = typeof(T);
        TableAttr = type.GetCustomAttribute<TableAttribute>();
        TableName = RequireSafeIdentifier(
            !string.IsNullOrEmpty(TableAttr?.Name) ? TableAttr.Name! : type.Name, type, "table");
        ExplicitSchemaName = !string.IsNullOrEmpty(TableAttr?.Schema)
            ? RequireSafeIdentifier(TableAttr.Schema!, type, "schema")
            : null;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreAttribute>() is null)
            .ToArray();

        var cols = new List<ColumnMetadata>(props.Length);
        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = RequireSafeIdentifier(
                !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : prop.Name, type, "column");
            cols.Add(new ColumnMetadata(prop, attr, colName));
        }

        Columns = cols;
        ColumnsByColumnName = cols.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
        ColumnsByPropertyName = cols.ToDictionary(c => c.Property.Name, c => c, StringComparer.Ordinal);
        AllColumnNames = cols.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PrimaryKey = cols.FirstOrDefault(c => c.Attribute?.Primary == true);
        CompositeIndexes = type.GetCustomAttributes<CompositeIndexAttribute>().ToArray();

        var softDelete = type.GetCustomAttribute<SoftDeleteAttribute>();
        if (softDelete is not null)
        {
            SoftDeleteColumn =
                (ColumnsByPropertyName.TryGetValue(softDelete.TimestampColumn, out var byProp) ? byProp : null)
                ?? (ColumnsByColumnName.TryGetValue(softDelete.TimestampColumn, out var byCol) ? byCol : null)
                ?? throw new InvalidOperationException(
                    $"[SoftDelete] on '{type.Name}' references '{softDelete.TimestampColumn}', which is not a mapped property/column.");
        }

        Materializer = new Materializer<T>();
    }

    /// <summary>
    /// Rejects any mapped identifier containing a double quote, a NUL, or a newline before it can
    /// reach generated SQL. Identifiers normally originate from compile-time attributes, so
    /// this is defence in depth: combined with <see cref="PostgreSqlDialect.Quote"/> at the render
    /// sites it makes identifier injection structurally impossible rather than merely unlikely.
    /// </summary>
    private static string RequireSafeIdentifier(string identifier, Type owner, string kind)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new InvalidOperationException(
                $"Entity '{owner.Name}' declares an empty {kind} name.");

        foreach (var c in identifier)
        {
            if (c is '"' or '\0' or '\n' or '\r')
                throw new InvalidOperationException(
                    $"Entity '{owner.Name}' declares the {kind} name '{identifier}', which contains " +
                    $"a character that is not permitted in a PostgreSQL identifier.");
        }

        return identifier;
    }

    /// <summary>
    /// Resolves a property-name / column-name string to its metadata. Accepts both forms
    /// (property name or mapped column name) for caller convenience.
    /// </summary>
    public static ColumnMetadata? TryResolve(string nameOrColumn)
    {
        if (ColumnsByColumnName.TryGetValue(nameOrColumn, out var byCol)) return byCol;
        if (ColumnsByPropertyName.TryGetValue(nameOrColumn, out var byProp)) return byProp;
        return null;
    }

    /// <summary>
    /// Resolves and throws with a helpful message if the name is unknown. Used by string-
    /// parameter APIs (e.g. <c>GetByColumnAsync(column, value)</c>) to prevent SQL injection
    /// via arbitrary column names.
    /// </summary>
    public static ColumnMetadata RequireColumn(string nameOrColumn)
    {
        var col = TryResolve(nameOrColumn);
        if (col is not null) return col;
        throw new ArgumentException(
            $"Column '{nameOrColumn}' is not defined on entity '{typeof(T).Name}'.",
            nameof(nameOrColumn));
    }

    public static ColumnMetadata RequirePrimaryKey()
    {
        if (PrimaryKey is null)
            throw new InvalidOperationException(
                $"Entity '{typeof(T).Name}' has no property marked with [Column(Primary = true)].");
        return PrimaryKey;
    }
}

/// <summary>
/// Per-column reflected metadata. Lazily computed fields are populated on first access.
/// </summary>
internal sealed class ColumnMetadata
{
    public PropertyInfo Property { get; }
    public ColumnAttribute? Attribute { get; }
    public string ColumnName { get; }
    public bool IsAutoIncrement => Attribute?.AutoIncrement == true;
    public bool IsPrimary => Attribute?.Primary == true;
    public StorageType EffectiveStorageType => Attribute?.StorageType ?? StorageType.Default;

    private Func<object, object?>? _getter;
    private Action<object, object?>? _setter;

    public ColumnMetadata(PropertyInfo prop, ColumnAttribute? attr, string columnName)
    {
        Property = prop;
        Attribute = attr;
        ColumnName = columnName;
    }

    /// <summary>Compiled getter — avoids PropertyInfo.GetValue reflection cost.</summary>
    public Func<object, object?> Get => _getter ??= BuildGetter();

    /// <summary>Compiled setter.</summary>
    public Action<object, object?> Set => _setter ??= BuildSetter();

    private Func<object, object?> BuildGetter()
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var cast = Expression.Convert(instance, Property.DeclaringType!);
        var access = Expression.Property(cast, Property);
        var box = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(box, instance).Compile();
    }

    private Action<object, object?> BuildSetter()
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var castInstance = Expression.Convert(instance, Property.DeclaringType!);
        var castValue = Expression.Convert(value, Property.PropertyType);
        var assign = Expression.Assign(Expression.Property(castInstance, Property), castValue);
        return Expression.Lambda<Action<object, object?>>(assign, instance, value).Compile();
    }
}
