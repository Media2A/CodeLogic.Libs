using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CL.PostgreSQL.Models;

namespace CL.PostgreSQL.Core;

/// <summary>
/// Translates LINQ lambda expressions into PostgreSQL WHERE clause fragments and parameter dictionaries.
/// Supports binary comparisons, logical operators, method calls (Contains, StartsWith, EndsWith),
/// and null checks. Uses double-quoted identifiers per PostgreSQL convention.
/// </summary>
internal sealed class PostgreSQLExpressionVisitor : ExpressionVisitor
{
    private readonly StringBuilder _sql = new();
    private readonly Dictionary<string, object?> _parameters = new();
    private int _paramCounter;
    private readonly string _tableAlias;

    public PostgreSQLExpressionVisitor(string tableAlias = "")
    {
        _tableAlias = tableAlias;
    }

    public static (string Sql, Dictionary<string, object?> Parameters) Translate<T>(
        Expression<Func<T, bool>> predicate,
        string tableAlias = "")
    {
        var visitor = new PostgreSQLExpressionVisitor(tableAlias);
        visitor.Visit(predicate.Body);
        return (visitor._sql.ToString(), visitor._parameters);
    }

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
        if (node.Expression is ParameterExpression)
        {
            var colName = GetColumnName(node.Member);
            if (!string.IsNullOrEmpty(_tableAlias))
                _sql.Append($"\"{_tableAlias}\".\"{colName}\"");
            else
                _sql.Append($"\"{colName}\"");
        }
        else
        {
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
        switch (node.Method.Name)
        {
            case "Contains" when node.Object is MemberExpression containsMember
                                  && node.Arguments.Count == 1:
            {
                var col = GetColumnName(containsMember.Member);
                if (!string.IsNullOrEmpty(_tableAlias))
                    _sql.Append($"\"{_tableAlias}\".\"{col}\"");
                else
                    _sql.Append($"\"{col}\"");

                _sql.Append(" LIKE ");
                var val = GetValue(node.Arguments[0]);
                AddParameter($"%{val}%");
                break;
            }
            case "StartsWith" when node.Object is MemberExpression startMember
                                    && node.Arguments.Count == 1:
            {
                var col = GetColumnName(startMember.Member);
                if (!string.IsNullOrEmpty(_tableAlias))
                    _sql.Append($"\"{_tableAlias}\".\"{col}\"");
                else
                    _sql.Append($"\"{col}\"");

                _sql.Append(" LIKE ");
                var val = GetValue(node.Arguments[0]);
                AddParameter($"{val}%");
                break;
            }
            case "EndsWith" when node.Object is MemberExpression endMember
                                  && node.Arguments.Count == 1:
            {
                var col = GetColumnName(endMember.Member);
                if (!string.IsNullOrEmpty(_tableAlias))
                    _sql.Append($"\"{_tableAlias}\".\"{col}\"");
                else
                    _sql.Append($"\"{col}\"");

                _sql.Append(" LIKE ");
                var val = GetValue(node.Arguments[0]);
                AddParameter($"%{val}");
                break;
            }
            case "Contains" when node.Arguments.Count == 2:
            {
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

    private static object? GetValue(Expression expression)
    {
        if (expression is MemberExpression member)
        {
            if (member.Member is PropertyInfo prop)
                return prop.GetGetMethod()?.Invoke(GetValue(member.Expression!), null);
            if (member.Member is FieldInfo field)
                return field.GetValue(GetValue(member.Expression!));
        }
        return Expression.Lambda(expression).Compile().DynamicInvoke();
    }
}
