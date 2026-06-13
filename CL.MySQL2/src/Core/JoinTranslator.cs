using System.Linq.Expressions;
using System.Reflection;
using CL.MySQL2.Models;

namespace CL.MySQL2.Core;

/// <summary>
/// Translation helpers specific to typed joins: turning key selectors into the
/// <c>ON</c> clause and turning a two-source result selector into a SELECT list +
/// ordered column descriptors. Predicate (<c>WHERE</c>) translation is delegated to
/// <see cref="MySqlExpressionVisitor.TranslateMulti"/>; this class only covers the
/// column-reference shapes that joins introduce.
/// </summary>
internal static class JoinTranslator
{
    /// <summary>
    /// Extracts the key columns of a join key selector, each qualified by
    /// <paramref name="alias"/>. Supports a single member (<c>o =&gt; o.CustomerId</c>)
    /// and a composite anonymous key (<c>o =&gt; new { o.A, o.B }</c>).
    /// </summary>
    public static List<string> KeyColumns(LambdaExpression keySelector, string alias)
    {
        var body = Unwrap(keySelector.Body);
        if (body is NewExpression ne)
        {
            if (ne.Arguments.Count == 0)
                throw new NotSupportedException("Join key selector produced an empty composite key.");
            return ne.Arguments.Select(a => ColumnRef(a, alias)).ToList();
        }
        return [ColumnRef(body, alias)];
    }

    /// <summary>
    /// Builds the <c>ON</c> clause by pairing left and right key columns positionally.
    /// </summary>
    public static string OnClause(
        LambdaExpression leftKey, string leftAlias,
        LambdaExpression rightKey, string rightAlias)
    {
        var left = KeyColumns(leftKey, leftAlias);
        var right = KeyColumns(rightKey, rightAlias);
        if (left.Count != right.Count)
            throw new ArgumentException(
                $"Join key arity mismatch: left key has {left.Count} column(s), right key has {right.Count}.");

        return string.Join(" AND ", left.Zip(right, (l, r) => $"{l} = {r}"));
    }

    /// <summary>
    /// Translates an <c>ORDER BY</c> selector member to its qualified column reference.
    /// </summary>
    public static string OrderColumn(
        Expression body, IReadOnlyDictionary<ParameterExpression, string> aliasMap)
        => ColumnRef(body, aliasMap);

    /// <summary>
    /// Walks a two-source result selector and produces the ordered
    /// (SQL fragment, alias, CLR type) column descriptors that
    /// <see cref="ProjectionCompiler.CompileFromColumns{T, TResult}"/> consumes.
    /// Supports anonymous types, positional records, member-init, and a single
    /// scalar column — mirroring the single-source <see cref="ProjectionCompiler"/>.
    /// </summary>
    public static List<(string Sql, string Alias, Type CLRType)> ProjectionColumns(
        Expression body, IReadOnlyDictionary<ParameterExpression, string> aliasMap)
    {
        var columns = new List<(string Sql, string Alias, Type CLRType)>();

        switch (body)
        {
            case NewExpression ne:
                CollectFromNew(ne, aliasMap, columns);
                break;

            case MemberInitExpression mi:
                CollectFromNew((NewExpression)mi.NewExpression, aliasMap, columns);
                foreach (var binding in mi.Bindings)
                {
                    if (binding is not MemberAssignment ma) continue;
                    columns.Add((
                        ColumnRef(ma.Expression, aliasMap),
                        ma.Member.Name,
                        MemberType(ma.Member)));
                }
                break;

            case MemberExpression me when me.Expression is ParameterExpression:
                columns.Add((ColumnRef(me, aliasMap), me.Member.Name, MemberType(me.Member)));
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported join projection shape: {body.NodeType} ({body}). " +
                    "Select member columns from either side, e.g. (l, r) => new { l.Id, r.Name }.");
        }

        return columns;
    }

    private static void CollectFromNew(
        NewExpression ne,
        IReadOnlyDictionary<ParameterExpression, string> aliasMap,
        List<(string Sql, string Alias, Type CLRType)> columns)
    {
        for (var i = 0; i < ne.Arguments.Count; i++)
        {
            var arg = Unwrap(ne.Arguments[i]);
            var alias = ne.Members is { } members && i < members.Count ? members[i].Name : $"col{i}";
            columns.Add((ColumnRef(arg, aliasMap), alias, ExprType(arg)));
        }
    }

    // ── Column reference resolution ──────────────────────────────────────────

    private static string ColumnRef(Expression expr, string alias)
    {
        expr = Unwrap(expr);
        if (expr is MemberExpression m && m.Expression is ParameterExpression)
            return $"`{alias}`.`{GetColumnName(m.Member)}`";
        throw new NotSupportedException(
            $"Join key/column must be a direct member access on a source parameter, got: {expr.NodeType} ({expr}).");
    }

    private static string ColumnRef(
        Expression expr, IReadOnlyDictionary<ParameterExpression, string> aliasMap)
    {
        expr = Unwrap(expr);
        if (expr is MemberExpression m && m.Expression is ParameterExpression pe
            && aliasMap.TryGetValue(pe, out var alias))
            return $"`{alias}`.`{GetColumnName(m.Member)}`";
        throw new NotSupportedException(
            $"Join column must be a direct member access on a join source, got: {expr.NodeType} ({expr}).");
    }

    private static Expression Unwrap(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
            e = u.Operand;
        return e;
    }

    private static Type ExprType(Expression e) =>
        e is MemberExpression m ? MemberType(m.Member) : e.Type;

    private static Type MemberType(MemberInfo member) =>
        (member as PropertyInfo)?.PropertyType
        ?? (member as FieldInfo)?.FieldType
        ?? typeof(object);

    private static string GetColumnName(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<ColumnAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : member.Name;
    }
}
