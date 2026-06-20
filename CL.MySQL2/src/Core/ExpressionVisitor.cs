using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CL.MySQL2.Models;

namespace CL.MySQL2.Core;

/// <summary>
/// Translates LINQ lambda expressions into MySQL WHERE clause fragments and parameter dictionaries.
/// Supports binary comparisons, logical operators, method calls (Contains, StartsWith, EndsWith),
/// and null checks.
/// </summary>
internal sealed class MySqlExpressionVisitor : ExpressionVisitor
{
    private readonly StringBuilder _sql = new();
    private readonly Dictionary<string, object?> _parameters = new();
    private int _paramCounter;
    private readonly string _tableAlias;
    private readonly IReadOnlyDictionary<ParameterExpression, string>? _aliasMap;
    private StorageType _currentComparisonStorageType = StorageType.Default;

    public MySqlExpressionVisitor(string tableAlias = "")
    {
        _tableAlias = tableAlias;
    }

    private MySqlExpressionVisitor(IReadOnlyDictionary<ParameterExpression, string> aliasMap)
    {
        _tableAlias = string.Empty;
        _aliasMap = aliasMap;
    }

    /// <summary>
    /// Translates the body of a predicate lambda into a SQL WHERE fragment.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="predicate">The lambda expression to translate.</param>
    /// <param name="tableAlias">Optional table alias prefix for column references.</param>
    /// <returns>SQL fragment and parameter dictionary.</returns>
    public static (string Sql, Dictionary<string, object?> Parameters) Translate<T>(
        Expression<Func<T, bool>> predicate,
        string tableAlias = "")
    {
        var visitor = new MySqlExpressionVisitor(tableAlias);
        visitor.Visit(predicate.Body);
        return (visitor._sql.ToString(), visitor._parameters);
    }

    /// <summary>
    /// Translates a multi-source predicate (e.g. a join predicate
    /// <c>(l, r) =&gt; l.X == r.Y &amp;&amp; l.Active</c>) into a SQL fragment whose column
    /// references are qualified by the alias mapped to each lambda parameter.
    /// </summary>
    /// <param name="predicate">The predicate lambda body to translate.</param>
    /// <param name="aliasMap">Maps each lambda parameter to its table alias (e.g. l→t0, r→t1).</param>
    public static (string Sql, Dictionary<string, object?> Parameters) TranslateMulti(
        LambdaExpression predicate,
        IReadOnlyDictionary<ParameterExpression, string> aliasMap)
    {
        var visitor = new MySqlExpressionVisitor(aliasMap);
        visitor.Visit(predicate.Body);
        return (visitor._sql.ToString(), visitor._parameters);
    }

    /// <summary>
    /// Translates a member selector lambda into a column name string.
    /// </summary>
    public static string TranslateSelector<T, TKey>(Expression<Func<T, TKey>> selector)
    {
        return selector.Body switch
        {
            MemberExpression member => GetColumnName(member.Member),
            UnaryExpression { Operand: MemberExpression inner } => GetColumnName(inner.Member),
            _ => throw new NotSupportedException($"Unsupported selector: {selector}")
        };
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        _sql.Append('(');

        // Resolve StorageType from whichever side is an entity member, so the
        // value side gets the correct binary conversion.
        var memberExpr = GetEntityMember(node.Left) ?? GetEntityMember(node.Right);
        var prevStorageType = _currentComparisonStorageType;
        if (memberExpr is not null)
            _currentComparisonStorageType = ResolveStorageType(memberExpr.Member);

        Visit(node.Left);

        var op = node.NodeType switch
        {
            ExpressionType.Equal              => " = ",
            ExpressionType.NotEqual           => " != ",
            ExpressionType.GreaterThan        => " > ",
            ExpressionType.GreaterThanOrEqual => " >= ",
            ExpressionType.LessThan           => " < ",
            ExpressionType.LessThanOrEqual    => " <= ",
            ExpressionType.AndAlso            => " AND ",
            ExpressionType.OrElse             => " OR ",
            ExpressionType.And                => " AND ",
            ExpressionType.Or                 => " OR ",
            _ => throw new NotSupportedException($"Unsupported binary operator: {node.NodeType}")
        };

        // Handle x.Prop == null → IS NULL
        if (node.NodeType == ExpressionType.Equal && IsNullConstant(node.Right))
        {
            _sql.Append(" IS NULL)");
            _currentComparisonStorageType = prevStorageType;
            return node;
        }
        if (node.NodeType == ExpressionType.NotEqual && IsNullConstant(node.Right))
        {
            _sql.Append(" IS NOT NULL)");
            _currentComparisonStorageType = prevStorageType;
            return node;
        }
        // Handle null == x.Prop
        if (node.NodeType == ExpressionType.Equal && IsNullConstant(node.Left))
        {
            // Already wrote left (null constant), rewrite
            _sql.Clear();
            _sql.Append('(');
            Visit(node.Right);
            _sql.Append(" IS NULL)");
            _currentComparisonStorageType = prevStorageType;
            return node;
        }

        _sql.Append(op);
        Visit(node.Right);
        _sql.Append(')');

        _currentComparisonStorageType = prevStorageType;
        return node;
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        // Nullable<T>.Value: pass through to the inner value column.
        if (node.Member.Name == "Value"
            && node.Expression is not null
            && Nullable.GetUnderlyingType(node.Expression.Type) is not null)
        {
            Visit(node.Expression);
            return node;
        }

        if (node.Expression is ParameterExpression)
        {
            // It's a property/field access on the entity parameter
            _sql.Append(QualifyColumn(node.Member, node.Expression));
        }
        else
        {
            // It's a captured variable / closure member — evaluate it
            var value = GetValue(node);
            AddParameter(value);
        }

        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        AddParameter(node.Value);
        return node;
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        if (node.NodeType == ExpressionType.Not)
        {
            _sql.Append("NOT ");
            Visit(node.Operand);
            return node;
        }
        if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            return Visit(node.Operand);
        }
        return base.VisitUnary(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // string.IsNullOrEmpty(x.Col) → (col IS NULL OR col = '')
        if (node.Method.DeclaringType == typeof(string)
            && node.Method.Name == nameof(string.IsNullOrEmpty)
            && node.Arguments.Count == 1
            && node.Arguments[0] is MemberExpression m
            && m.Expression is ParameterExpression)
        {
            var colRef = QualifyColumn(m.Member, m.Expression);
            _sql.Append($"({colRef} IS NULL OR {colRef} = '')");
            return node;
        }

        switch (node.Method.Name)
        {
            case "Contains" when node.Object is MemberExpression containsMember
                                  && node.Arguments.Count == 1:
            {
                _sql.Append(QualifyColumn(containsMember.Member, containsMember.Expression));
                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"%{val}%");
                break;
            }
            case "StartsWith" when node.Object is MemberExpression startMember
                                    && node.Arguments.Count == 1:
            {
                _sql.Append(QualifyColumn(startMember.Member, startMember.Expression));
                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"{val}%");
                break;
            }
            case "EndsWith" when node.Object is MemberExpression endMember
                                  && node.Arguments.Count == 1:
            {
                _sql.Append(QualifyColumn(endMember.Member, endMember.Expression));
                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"%{val}");
                break;
            }
            case "Contains" when node.Arguments.Count == 2:
            {
                // Static Enumerable.Contains(collection, item) or IList.Contains
                var collection = GetValue(node.Arguments[0]);
                var entityMember = GetEntityMember(node.Arguments[1]);
                var prevStorageType = _currentComparisonStorageType;
                if (entityMember is not null)
                    _currentComparisonStorageType = ResolveStorageType(entityMember.Member);

                Visit(node.Arguments[1]);
                _sql.Append(" IN (");
                if (collection is System.Collections.IEnumerable enumerable)
                {
                    bool first = true;
                    foreach (var item in enumerable)
                    {
                        if (!first) _sql.Append(", ");
                        AddParameter(item);
                        first = false;
                    }
                }
                _sql.Append(')');
                _currentComparisonStorageType = prevStorageType;
                break;
            }
            default:
                // Try to evaluate the method call as a constant
                try
                {
                    var value = GetValue(node);
                    AddParameter(value);
                }
                catch
                {
                    throw new NotSupportedException($"Unsupported method call: {node.Method.Name}");
                }
                break;
        }

        return node;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AddParameter(object? value)
    {
        var paramName = $"@p{_paramCounter++}";
        _parameters[paramName] = TypeConverter.ToDbValue(value, _currentComparisonStorageType);
        _sql.Append(paramName);
    }

    private static bool IsNullConstant(Expression expr) =>
        expr is ConstantExpression { Value: null };

    private static string GetColumnName(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<ColumnAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : member.Name;
    }

    /// <summary>
    /// Builds a backtick-quoted column reference, qualified by the alias mapped to
    /// <paramref name="owner"/> when a multi-source alias map is in play, or by the single
    /// <c>_tableAlias</c> otherwise. With no alias the column is left unqualified — exactly
    /// the single-table behaviour that predates joins.
    /// </summary>
    private string QualifyColumn(MemberInfo member, Expression? owner)
    {
        var col = GetColumnName(member);
        var alias = ResolveAlias(owner);
        return string.IsNullOrEmpty(alias) ? $"`{col}`" : $"`{alias}`.`{col}`";
    }

    private string ResolveAlias(Expression? owner)
    {
        if (_aliasMap is not null && owner is ParameterExpression pe
            && _aliasMap.TryGetValue(pe, out var alias))
            return alias;
        return _tableAlias;
    }

    private static MemberExpression? GetEntityMember(Expression expr)
    {
        var e = expr;
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
            e = u.Operand;
        return e is MemberExpression { Expression: ParameterExpression } me ? me : null;
    }

    private static StorageType ResolveStorageType(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<ColumnAttribute>();
        return attr?.StorageType ?? StorageType.Default;
    }

    private static object? GetValue(Expression expression) => ClosureEvaluator.Evaluate(expression);

    /// <summary>
    /// Escapes LIKE special characters (%, _, \) in a user-supplied value so it is treated
    /// as a literal, not as a wildcard pattern. Non-string values pass through unchanged.
    /// </summary>
    private static object? EscapeLikeValue(object? value)
    {
        if (value is not string s) return value;
        return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
