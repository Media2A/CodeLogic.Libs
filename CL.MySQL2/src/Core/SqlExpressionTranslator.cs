using System.Linq.Expressions;
using System.Reflection;

namespace CL.MySQL2.Core;

/// <summary>
/// Translates column-level C# expressions into MySQL SQL fragments. This is the
/// read-side counterpart to <see cref="MySqlExpressionVisitor"/> (which handles WHERE
/// predicates). Covers what <c>Select</c> / grouped-<c>Select</c> / <c>GroupBy</c> keys
/// need: column access, arithmetic, <c>SqlFn.*</c>, ternary, aggregates.
/// </summary>
internal static class SqlExpressionTranslator
{
    /// <summary>
    /// Translate an expression to SQL + its CLR result type, inside a context that may
    /// or may not be grouped. When <paramref name="groupingParam"/> is non-null the
    /// expression may reference <c>g.Key</c> and call <c>g.Sum/Avg/Min/Max/Count</c>.
    /// </summary>
    public static (string Sql, Type ClrType) Translate(
        Expression expr,
        ParameterExpression? rowParam,
        ParameterExpression? groupingParam,
        Expression? groupKeyExpr)
    {
        return new Visitor(rowParam, groupingParam, groupKeyExpr).Visit(expr);
    }

    private sealed class Visitor
    {
        private readonly ParameterExpression? _row;
        private readonly ParameterExpression? _group;
        private readonly Expression? _groupKey;

        public Visitor(ParameterExpression? row, ParameterExpression? group, Expression? groupKey)
        {
            _row = row;
            _group = group;
            _groupKey = groupKey;
        }

        public (string Sql, Type ClrType) Visit(Expression node)
        {
            return node switch
            {
                MemberExpression m => VisitMember(m),
                BinaryExpression b => VisitBinary(b),
                ConditionalExpression c => VisitConditional(c),
                MethodCallExpression mc => VisitMethodCall(mc),
                UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
                    => (Visit(u.Operand).Sql, u.Type),
                UnaryExpression { NodeType: ExpressionType.Negate } u
                    => ($"-({Visit(u.Operand).Sql})", u.Type),
                ConstantExpression ce => ($"{FormatConstant(ce.Value)}", node.Type),
                NewExpression ne => VisitNew(ne),
                _ => throw new NotSupportedException($"Unsupported column expression: {node.NodeType} ({node})")
            };
        }

        // new { Dow = ..., Hour = ... } — used when a GroupBy key is composite; the
        // caller collects each member separately, so here we just pick the first arg
        // for single-expression contexts.
        private (string Sql, Type ClrType) VisitNew(NewExpression ne)
        {
            if (ne.Arguments.Count == 1) return Visit(ne.Arguments[0]);
            throw new NotSupportedException(
                "NewExpression with multiple members should be decomposed by the caller (see ProjectionCompiler / GroupedQuery key handling).");
        }

        private (string Sql, Type ClrType) VisitMember(MemberExpression m)
        {
            // g.Key (reference to the grouping key itself — may be scalar or composite)
            if (_group is not null && m.Expression == _group && m.Member.Name == "Key")
            {
                if (_groupKey is null)
                    throw new NotSupportedException("g.Key used without a known group key expression.");
                return Visit(_groupKey);
            }

            // g.Key.Prop — reach into a composite anonymous key
            if (_group is not null && m.Expression is MemberExpression inner
                && inner.Expression == _group && inner.Member.Name == "Key"
                && _groupKey is NewExpression keyNew)
            {
                var idx = keyNew.Members!.ToList().FindIndex(mem => mem.Name == m.Member.Name);
                if (idx < 0) throw new NotSupportedException($"Group key member '{m.Member.Name}' not found.");
                return Visit(keyNew.Arguments[idx]);
            }

            // x.Col on the row parameter → column reference
            if (m.Expression is ParameterExpression pe && pe == _row)
            {
                var colName = GetColumnName(m.Member);
                var clrType = (m.Member as PropertyInfo)?.PropertyType
                              ?? (m.Member as FieldInfo)?.FieldType
                              ?? typeof(object);
                return ($"`{colName}`", clrType);
            }

            // Captured closure member — evaluate to a literal. Primitives only for column
            // expressions; WHERE-side parameter binding is handled by MySqlExpressionVisitor.
            var value = Expression.Lambda(m).Compile().DynamicInvoke();
            return (FormatConstant(value), m.Type);
        }

        private (string Sql, Type ClrType) VisitBinary(BinaryExpression b)
        {
            var op = b.NodeType switch
            {
                ExpressionType.Add                => "+",
                ExpressionType.Subtract           => "-",
                ExpressionType.Multiply           => "*",
                ExpressionType.Divide             => "/",
                ExpressionType.Modulo             => "%",
                ExpressionType.Equal              => "=",
                ExpressionType.NotEqual           => "!=",
                ExpressionType.GreaterThan        => ">",
                ExpressionType.GreaterThanOrEqual => ">=",
                ExpressionType.LessThan           => "<",
                ExpressionType.LessThanOrEqual    => "<=",
                ExpressionType.AndAlso            => "AND",
                ExpressionType.OrElse             => "OR",
                _ => throw new NotSupportedException($"Unsupported binary operator: {b.NodeType}")
            };

            var left = Visit(b.Left);
            var right = Visit(b.Right);
            return ($"({left.Sql} {op} {right.Sql})", b.Type);
        }

        // x.IsOnline ? 100.0 : 0.0  →  CASE WHEN is_online = 1 THEN 100 ELSE 0 END
        private (string Sql, Type ClrType) VisitConditional(ConditionalExpression c)
        {
            var cond = Visit(c.Test);
            var thenExpr = Visit(c.IfTrue);
            var elseExpr = Visit(c.IfFalse);
            return ($"(CASE WHEN {cond.Sql} THEN {thenExpr.Sql} ELSE {elseExpr.Sql} END)", c.Type);
        }

        private (string Sql, Type ClrType) VisitMethodCall(MethodCallExpression mc)
        {
            // SqlFn.* marker calls
            if (mc.Method.DeclaringType == typeof(SqlFn))
                return VisitSqlFn(mc);

            // IGrouping<TKey, T>.Sum/Average/Min/Max/Count/Any aggregate calls
            if (_group is not null && mc.Arguments.Count > 0 && mc.Arguments[0] == _group)
                return VisitAggregate(mc);

            throw new NotSupportedException(
                $"Unsupported method in column expression: {mc.Method.DeclaringType?.Name}.{mc.Method.Name}");
        }

        private (string Sql, Type ClrType) VisitSqlFn(MethodCallExpression mc)
        {
            string Arg(int i) => Visit(mc.Arguments[i]).Sql;

            switch (mc.Method.Name)
            {
                case nameof(SqlFn.Year):       return ($"YEAR({Arg(0)})", typeof(int));
                case nameof(SqlFn.Month):      return ($"MONTH({Arg(0)})", typeof(int));
                case nameof(SqlFn.Day):        return ($"DAY({Arg(0)})", typeof(int));
                case nameof(SqlFn.Hour):       return ($"HOUR({Arg(0)})", typeof(int));
                case nameof(SqlFn.Minute):     return ($"MINUTE({Arg(0)})", typeof(int));
                // MySQL DAYOFWEEK is 1..7 starting Sunday; .NET DayOfWeek is 0..6 starting Sunday.
                case nameof(SqlFn.DayOfWeek):  return ($"(DAYOFWEEK({Arg(0)}) - 1)", typeof(int));
                case nameof(SqlFn.Date):       return ($"DATE({Arg(0)})", typeof(DateTime));
                case nameof(SqlFn.BucketUtc):  return ($"FROM_UNIXTIME(FLOOR(UNIX_TIMESTAMP({Arg(0)}) / {Arg(1)}) * {Arg(1)})", typeof(DateTime));
                case nameof(SqlFn.Coalesce):
                {
                    var args = ((NewArrayExpression)mc.Arguments[0]).Expressions
                        .Select(a => Visit(a).Sql);
                    return ($"COALESCE({string.Join(", ", args)})", mc.Method.ReturnType);
                }
                case nameof(SqlFn.IfNull):     return ($"IFNULL({Arg(0)}, {Arg(1)})", mc.Method.ReturnType);
                case nameof(SqlFn.Lower):      return ($"LOWER({Arg(0)})", typeof(string));
                case nameof(SqlFn.Upper):      return ($"UPPER({Arg(0)})", typeof(string));
                case nameof(SqlFn.Concat):
                {
                    var args = ((NewArrayExpression)mc.Arguments[0]).Expressions
                        .Select(a => Visit(a).Sql);
                    return ($"CONCAT({string.Join(", ", args)})", typeof(string));
                }
                case nameof(SqlFn.Like):       return ($"({Arg(0)} LIKE {Arg(1)})", typeof(bool));
                case nameof(SqlFn.Round):      return ($"ROUND({Arg(0)}, {Arg(1)})", typeof(double));
                case nameof(SqlFn.Floor):      return ($"FLOOR({Arg(0)})", typeof(double));
                case nameof(SqlFn.Ceiling):    return ($"CEILING({Arg(0)})", typeof(double));
                default:
                    throw new NotSupportedException($"Unsupported SqlFn: {mc.Method.Name}");
            }
        }

        /// <summary>
        /// Handle <c>g.Sum(x =&gt; x.Foo)</c>, <c>g.Average(...)</c>, <c>g.Min/Max(...)</c>,
        /// <c>g.Count()</c>, <c>g.Count(x =&gt; pred)</c>, <c>g.Any()</c>, <c>g.Any(pred)</c>.
        /// The inner lambda's row parameter rebinds to the row param of this visitor.
        /// </summary>
        private (string Sql, Type ClrType) VisitAggregate(MethodCallExpression mc)
        {
            var method = mc.Method.Name;

            switch (method)
            {
                case nameof(Enumerable.Count) when mc.Arguments.Count == 1:
                    return ("COUNT(*)", typeof(int));

                case nameof(Enumerable.Count) when mc.Arguments.Count == 2:
                {
                    var lambda = (LambdaExpression)StripQuotes(mc.Arguments[1]);
                    var innerVisitor = new Visitor(lambda.Parameters[0], _group, _groupKey);
                    var predicate = innerVisitor.Visit(lambda.Body);
                    return ($"SUM(CASE WHEN {predicate.Sql} THEN 1 ELSE 0 END)", typeof(int));
                }

                case nameof(Enumerable.Any) when mc.Arguments.Count == 1:
                    return ("(COUNT(*) > 0)", typeof(bool));

                case nameof(Enumerable.Any) when mc.Arguments.Count == 2:
                {
                    var lambda = (LambdaExpression)StripQuotes(mc.Arguments[1]);
                    var innerVisitor = new Visitor(lambda.Parameters[0], _group, _groupKey);
                    var predicate = innerVisitor.Visit(lambda.Body);
                    return ($"(SUM(CASE WHEN {predicate.Sql} THEN 1 ELSE 0 END) > 0)", typeof(bool));
                }

                case nameof(Enumerable.Sum):
                case nameof(Enumerable.Average):
                case nameof(Enumerable.Min):
                case nameof(Enumerable.Max):
                {
                    var sqlFn = method switch
                    {
                        nameof(Enumerable.Sum)     => "SUM",
                        nameof(Enumerable.Average) => "AVG",
                        nameof(Enumerable.Min)     => "MIN",
                        nameof(Enumerable.Max)     => "MAX",
                        _ => throw new NotSupportedException()
                    };

                    var lambda = (LambdaExpression)StripQuotes(mc.Arguments[1]);
                    var innerVisitor = new Visitor(lambda.Parameters[0], _group, _groupKey);
                    var inner = innerVisitor.Visit(lambda.Body);
                    // Type: Sum/Min/Max preserve inner type; AVG → double.
                    var outType = method == nameof(Enumerable.Average) ? typeof(double) : inner.ClrType;
                    return ($"{sqlFn}({inner.Sql})", outType);
                }
            }

            throw new NotSupportedException($"Unsupported aggregate: {method}");
        }

        private static Expression StripQuotes(Expression e)
        {
            while (e is UnaryExpression { NodeType: ExpressionType.Quote } u) e = u.Operand;
            return e;
        }

        private static string FormatConstant(object? v) => v switch
        {
            null => "NULL",
            bool b => b ? "1" : "0",
            string s => $"'{s.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "NULL"
        };

        private static string GetColumnName(MemberInfo member)
        {
            var attr = member.GetCustomAttribute<Models.ColumnAttribute>();
            return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : member.Name;
        }
    }
}
