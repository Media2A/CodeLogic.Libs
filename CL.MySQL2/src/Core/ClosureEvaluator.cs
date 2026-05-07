using System.Linq.Expressions;
using System.Reflection;

namespace CL.MySQL2.Core;

/// <summary>
/// Evaluates the runtime value of a closure / constant expression encountered while
/// translating a LINQ expression tree to SQL. The fast path handles the shapes that
/// account for nearly all real-world predicates — bare constants, member chains
/// rooted in a captured constant, and identity casts — using O(depth) reflection.
/// Only genuinely dynamic shapes fall back to <see cref="Expression.Lambda(Expression, ParameterExpression[])"/>
/// + Compile, so the typical <c>Where(x =&gt; x.Foo == localVar)</c> pays a field
/// read instead of a JIT compile per call.
/// </summary>
internal static class ClosureEvaluator
{
    /// <summary>
    /// Resolve the runtime value of an expression that does not reference a row parameter.
    /// Tries the structural fast path first; falls back to delegate compilation only when
    /// the expression has a shape the fast path does not recognise.
    /// </summary>
    public static object? Evaluate(Expression expression)
    {
        if (TryFastEvaluate(expression, out var v)) return v;
        return Expression.Lambda(expression).Compile().DynamicInvoke();
    }

    /// <summary>
    /// Attempt to resolve the value of an expression without compiling a delegate.
    /// Returns <c>true</c> for the recognised shapes, <c>false</c> otherwise.
    /// </summary>
    public static bool TryFastEvaluate(Expression expr, out object? value)
    {
        switch (expr)
        {
            case ConstantExpression ce:
                value = ce.Value;
                return true;

            case MemberExpression me:
                if (me.Expression is null)
                {
                    // static field / property
                    value = me.Member switch
                    {
                        FieldInfo fi    => fi.GetValue(null),
                        PropertyInfo pi => pi.GetValue(null),
                        _               => null
                    };
                    return true;
                }
                if (!TryFastEvaluate(me.Expression, out var target)) { value = null; return false; }
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
}
