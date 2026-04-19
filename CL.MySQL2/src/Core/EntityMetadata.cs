using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using CL.MySQL2.Models;
using MySqlConnector;

namespace CL.MySQL2.Core;

/// <summary>
/// Single source of truth for reflected metadata about entity type <typeparamref name="T"/>.
/// Populated once per type on first access and held for the lifetime of the AppDomain.
/// Replaces the per-file caches that previously lived in <c>Repository</c>,
/// <c>QueryBuilder</c>, and <c>SchemaAnalyzer</c>.
/// </summary>
internal static class EntityMetadata<T> where T : class
{
    public static readonly string TableName;
    public static readonly TableAttribute? TableAttr;
    public static readonly IReadOnlyList<ColumnMetadata> Columns;
    public static readonly IReadOnlyDictionary<string, ColumnMetadata> ColumnsByColumnName;
    public static readonly IReadOnlyDictionary<string, ColumnMetadata> ColumnsByPropertyName;
    public static readonly ColumnMetadata? PrimaryKey;
    public static readonly IReadOnlyList<CompositeIndexAttribute> CompositeIndexes;
    public static readonly IReadOnlySet<string> AllColumnNames;

    /// <summary>
    /// Compiled row materializer: reads a <see cref="MySqlDataReader"/> positioned on a row
    /// and returns a fully hydrated <typeparamref name="T"/>. Uses column ordinals resolved
    /// once per query (see <see cref="Materializer{T}.CompileForReader"/>).
    /// </summary>
    public static readonly Materializer<T> Materializer;

    static EntityMetadata()
    {
        var type = typeof(T);
        TableAttr = type.GetCustomAttribute<TableAttribute>();
        TableName = !string.IsNullOrEmpty(TableAttr?.Name) ? TableAttr.Name! : type.Name;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreAttribute>() is null)
            .ToArray();

        var cols = new List<ColumnMetadata>(props.Length);
        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : prop.Name;
            cols.Add(new ColumnMetadata(prop, attr, colName));
        }

        Columns = cols;
        ColumnsByColumnName = cols.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
        ColumnsByPropertyName = cols.ToDictionary(c => c.Property.Name, c => c, StringComparer.Ordinal);
        AllColumnNames = cols.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PrimaryKey = cols.FirstOrDefault(c => c.Attribute?.Primary == true);
        CompositeIndexes = type.GetCustomAttributes<CompositeIndexAttribute>().ToArray();

        Materializer = new Materializer<T>();
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
