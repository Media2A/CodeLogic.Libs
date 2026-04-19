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

    public MySqlExpressionVisitor(string tableAlias = "")
    {
        _tableAlias = tableAlias;
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
            return node;
        }
        if (node.NodeType == ExpressionType.NotEqual && IsNullConstant(node.Right))
        {
            _sql.Append(" IS NOT NULL)");
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
            return node;
        }

        _sql.Append(op);
        Visit(node.Right);
        _sql.Append(')');

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
            var colName = GetColumnName(node.Member);
            if (!string.IsNullOrEmpty(_tableAlias))
                _sql.Append($"`{_tableAlias}`.`{colName}`");
            else
                _sql.Append($"`{colName}`");
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
            var col = GetColumnName(m.Member);
            var colRef = !string.IsNullOrEmpty(_tableAlias) ? $"`{_tableAlias}`.`{col}`" : $"`{col}`";
            _sql.Append($"({colRef} IS NULL OR {colRef} = '')");
            return node;
        }

        switch (node.Method.Name)
        {
            case "Contains" when node.Object is MemberExpression containsMember
                                  && node.Arguments.Count == 1:
            {
                var col = GetColumnName(containsMember.Member);
                if (!string.IsNullOrEmpty(_tableAlias))
                    _sql.Append($"`{_tableAlias}`.`{col}`");
                else
                    _sql.Append($"`{col}`");

                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"%{val}%");
                break;
            }
            case "StartsWith" when node.Object is MemberExpression startMember
                                    && node.Arguments.Count == 1:
            {
                var col = GetColumnName(startMember.Member);
                if (!string.IsNullOrEmpty(_tableAlias))
                    _sql.Append($"`{_tableAlias}`.`{col}`");
                else
                    _sql.Append($"`{col}`");

                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"{val}%");
                break;
            }
            case "EndsWith" when node.Object is MemberExpression endMember
                                  && node.Arguments.Count == 1:
            {
                var col = GetColumnName(endMember.Member);
                if (!string.IsNullOrEmpty(_tableAlias))
                    _sql.Append($"`{_tableAlias}`.`{col}`");
                else
                    _sql.Append($"`{col}`");

                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"%{val}");
                break;
            }
            case "Contains" when node.Arguments.Count == 2:
            {
                // Static Enumerable.Contains(collection, item) or IList.Contains
                var collection = GetValue(node.Arguments[0]);
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
        _parameters[paramName] = TypeConverter.ToDbValue(value);
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
    /// Reads the runtime value of a closure / constant expression without compiling a
    /// delegate. Covers the common cases (bare constant, field / property chains over a
    /// captured constant) in O(depth) reflection. Falls back to
    /// <see cref="Expression.Lambda(Expression, ParameterExpression[])"/> + Compile only
    /// for genuinely dynamic shapes, so predicates like <c>Where(x =&gt; x.Foo &gt;= since)</c>
    /// pay field-read cost, not JIT cost.
    /// </summary>
    private static object? GetValue(Expression expression)
    {
        if (TryFastEvaluate(expression, out var v)) return v;
        return Expression.Lambda(expression).Compile().DynamicInvoke();
    }

    private static bool TryFastEvaluate(Expression expr, out object? value)
    {
        switch (expr)
        {
            case ConstantExpression ce:
                value = ce.Value;
                return true;

            case MemberExpression me:
                if (!TryFastEvaluate(me.Expression!, out var target)) { value = null; return false; }
                value = me.Member switch
                {
                    FieldInfo fi    => fi.GetValue(target),
                    PropertyInfo pi => pi.GetValue(target),
                    _               => null
                };
                return true;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u:
                return TryFastEvaluate(u.Operand, out value);

            default:
                value = null;
                return false;
        }
    }

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
