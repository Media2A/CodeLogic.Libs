using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using MySqlConnector;

namespace CL.MySQL2.Core;

/// <summary>
/// Turns a projection lambda like <c>s =&gt; new { s.A, s.B }</c> or
/// <c>s =&gt; new Foo(s.A, s.B + 1)</c> into:
/// <list type="bullet">
///   <item>a SQL <c>SELECT</c> column list with aliases, and</item>
///   <item>a compiled <c>Func&lt;MySqlDataReader, TResult&gt;</c> that pulls values out of
///     the reader by ordinal and builds the projected shape.</item>
/// </list>
/// Supports anonymous types (the common case), positional records (ctor binding), and
/// simple member-init expressions.
/// </summary>
internal static class ProjectionCompiler
{
    // Cached per source-type+expression-signature. First call compiles; subsequent calls reuse.
    private static readonly ConcurrentDictionary<string, object> _cache = new();

    public readonly struct Compiled<T, TResult>
    {
        /// <summary>SQL fragment: comma-separated columns/aliases for SELECT list.</summary>
        public string SelectList { get; init; }
        /// <summary>Ordered list of (column expression, alias). Alias is what the reader sees.</summary>
        public IReadOnlyList<(string Sql, string Alias)> Columns { get; init; }
        /// <summary>Compiled row → TResult materializer keyed by reader shape (alias→ordinal).</summary>
        public Func<MySqlDataReader, TResult> Materializer { get; init; }
    }

    /// <summary>
    /// Compile a projection. Returns the SELECT list to embed in the SQL and a
    /// materializer that hydrates a <typeparamref name="TResult"/> from the reader.
    /// </summary>
    public static Compiled<T, TResult> Compile<T, TResult>(Expression<Func<T, TResult>> projection)
    {
        var key = $"{typeof(T).FullName}|{projection.ToString()}|{typeof(TResult).FullName}";
        if (_cache.TryGetValue(key, out var cached))
            return (Compiled<T, TResult>)cached;

        var result = Build(projection);
        _cache[key] = result!;
        return result;
    }

    /// <summary>
    /// Assemble a <see cref="Compiled{T, TResult}"/> from a set of pre-translated column
    /// expressions (as emitted by <see cref="SqlExpressionTranslator"/>). Used by
    /// <c>GroupedQuery</c> where the translator already produced the SELECT fragments.
    /// </summary>
    internal static Compiled<T, TResult> CompileFromColumns<T, TResult>(
        Expression projectionBody,
        List<(string Sql, string Alias, Type CLRType)> columns)
    {
        var selectList = string.Join(", ", columns.Select(c => $"{c.Sql} AS `{c.Alias}`"));
        var materializer = BuildMaterializer<TResult>(projectionBody, columns);

        return new Compiled<T, TResult>
        {
            SelectList = selectList,
            Columns = columns.Select(c => (c.Sql, c.Alias)).ToArray(),
            Materializer = materializer
        };
    }

    private static Compiled<T, TResult> Build<T, TResult>(Expression<Func<T, TResult>> projection)
    {
        // Collect (sqlExpr, alias) pairs describing what to select.
        var columns = new List<(string Sql, string Alias, Type CLRType)>();

        switch (projection.Body)
        {
            case NewExpression newExpr:
                // Anonymous type: new { s.A, Total = s.B * 2 }  — or positional record ctor.
                CollectFromNew(newExpr, columns);
                break;

            case MemberInitExpression mi:
                // new Foo { A = s.A, B = s.B }
                CollectFromNew((NewExpression)mi.NewExpression, columns);
                foreach (var binding in mi.Bindings)
                {
                    if (binding is MemberAssignment ma)
                    {
                        var (sql, _) = TranslateColumnExpression(ma.Expression);
                        columns.Add((sql, ma.Member.Name, ma.Member is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)ma.Member).FieldType));
                    }
                }
                break;

            case MemberExpression memberExpr:
                // Single-column projection: s => s.Foo
                var (colSql, colType) = TranslateColumnExpression(memberExpr);
                columns.Add((colSql, memberExpr.Member.Name, colType));
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported projection shape: {projection.Body.NodeType} in {projection}");
        }

        // Build SELECT list with safe aliases.
        var selectList = string.Join(", ",
            columns.Select(c => $"{c.Sql} AS `{c.Alias}`"));

        // Materializer: resolves alias → ordinal on first row, then reads by index.
        var materializer = BuildMaterializer<TResult>(projection.Body, columns);

        return new Compiled<T, TResult>
        {
            SelectList = selectList,
            Columns = columns.Select(c => (c.Sql, c.Alias)).ToArray(),
            Materializer = materializer
        };
    }

    private static void CollectFromNew(NewExpression newExpr, List<(string Sql, string Alias, Type CLRType)> columns)
    {
        for (var i = 0; i < newExpr.Arguments.Count; i++)
        {
            var arg = newExpr.Arguments[i];
            var (sql, type) = TranslateColumnExpression(arg);
            // Alias: member name for anonymous/record, or fall back to "col{i}"
            var alias = newExpr.Members is { } members && i < members.Count
                ? members[i].Name
                : $"col{i}";
            columns.Add((sql, alias, type));
        }
    }

    /// <summary>
    /// Translate a single column-level expression into a MySQL SQL fragment and carry
    /// the CLR type so the materializer knows how to convert. Only covers the shapes the
    /// SELECT visitor currently understands — the wider aggregation visitor (task #4)
    /// will extend this with SqlFn and aggregates.
    /// </summary>
    private static (string Sql, Type CLRType) TranslateColumnExpression(Expression expr)
    {
        switch (expr)
        {
            case MemberExpression m when m.Expression is ParameterExpression:
            {
                var prop = m.Member;
                var attr = prop.GetCustomAttribute<Models.ColumnAttribute>();
                var colName = !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : prop.Name;
                var clrType = (prop as PropertyInfo)?.PropertyType
                              ?? (prop as FieldInfo)?.FieldType
                              ?? typeof(object);
                return ($"`{colName}`", clrType);
            }

            case UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
                return TranslateColumnExpression(u.Operand);

            default:
                throw new NotSupportedException(
                    $"Unsupported projection expression: {expr.NodeType} ({expr}). " +
                    "Simple column access is supported; aggregation & SqlFn arrive with the grouped query work.");
        }
    }

    /// <summary>
    /// Build a delegate that reads aliased columns from <see cref="MySqlDataReader"/> and
    /// produces <typeparamref name="TResult"/>. Works for:
    /// <list type="bullet">
    ///   <item><c>new { A, B }</c> — ctor call in the same order as the SELECT list</item>
    ///   <item>positional records — same</item>
    ///   <item><c>new Foo { A = ... }</c> — member-init with property bindings</item>
    ///   <item>scalar — single column extracted by alias</item>
    /// </list>
    /// Materializer uses <c>GetOrdinal(alias)</c> once on first row via a closure cache.
    /// </summary>
    private static Func<MySqlDataReader, TResult> BuildMaterializer<TResult>(
        Expression body,
        List<(string Sql, string Alias, Type CLRType)> columns)
    {
        // Capture ordinals lazily on first call (reader may reorder columns based on SQL plan).
        int[]? ordinals = null;

        return reader =>
        {
            if (ordinals is null)
            {
                ordinals = new int[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                    ordinals[i] = reader.GetOrdinal(columns[i].Alias);
            }

            var values = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var o = ordinals[i];
                var raw = reader.IsDBNull(o) ? null : reader.GetValue(o);
                values[i] = TypeConverter.FromDbValue(raw, columns[i].CLRType);
            }

            return (TResult)Hydrate(body, values, columns)!;
        };
    }

    private static object? Hydrate(
        Expression body,
        object?[] values,
        List<(string Sql, string Alias, Type CLRType)> columns)
    {
        switch (body)
        {
            case NewExpression newExpr:
                // Constructor call (anonymous type or positional record). Arg count
                // matches the number of columns in the NEW expression, but member-init
                // bindings are handled below and may extend this.
                return Activator.CreateInstance(newExpr.Type, values.Take(newExpr.Arguments.Count).ToArray());

            case MemberInitExpression mi:
            {
                var ctorArgCount = ((NewExpression)mi.NewExpression).Arguments.Count;
                var instance = Activator.CreateInstance(mi.Type, values.Take(ctorArgCount).ToArray());
                var idx = ctorArgCount;
                foreach (var b in mi.Bindings)
                {
                    if (b is MemberAssignment ma)
                    {
                        if (ma.Member is PropertyInfo pi) pi.SetValue(instance, values[idx]);
                        else if (ma.Member is FieldInfo fi) fi.SetValue(instance, values[idx]);
                        idx++;
                    }
                }
                return instance;
            }

            case MemberExpression:
                // Scalar projection — first value is the result.
                return values[0];

            case MethodCallExpression:
            case BinaryExpression:
            case ConditionalExpression:
                // Scalar projection from an aggregate / expression (e.g. g.Average(...), g.Sum(...)).
                return values[0];

            default:
                throw new NotSupportedException($"Cannot hydrate expression type {body.NodeType}");
        }
    }
}
