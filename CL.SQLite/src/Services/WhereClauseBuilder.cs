using CL.SQLite.Models;

namespace CL.SQLite.Services;

/// <summary>
/// Builds SQL WHERE clauses from a list of <see cref="WhereCondition"/> objects,
/// using indexed parameter names (@p0, @p1, ...).
/// </summary>
internal static class WhereClauseBuilder
{
    public static (string Clause, Dictionary<string, object?> Parameters) Build(
        List<WhereCondition> conditions)
    {
        if (conditions.Count == 0)
            return (string.Empty, new Dictionary<string, object?>());

        var parameters = new Dictionary<string, object?>();
        var parts = new List<string>();
        var paramIndex = 0;

        for (int i = 0; i < conditions.Count; i++)
        {
            var cond = conditions[i];
            var paramName = $"@p{paramIndex++}";

            string fragment;

            if (cond.Value is null && (cond.Operator == "=" || cond.Operator == "IS"))
                fragment = $"\"{cond.Column}\" IS NULL";
            else if (cond.Value is null && (cond.Operator == "!=" || cond.Operator == "IS NOT"))
                fragment = $"\"{cond.Column}\" IS NOT NULL";
            else
            {
                parameters[paramName] = cond.Value;
                fragment = $"\"{cond.Column}\" {cond.Operator} {paramName}";
            }

            if (i > 0)
                parts.Add($" {cond.LogicalOperator} {fragment}");
            else
                parts.Add(fragment);
        }

        return ($" WHERE {string.Concat(parts)}", parameters);
    }
}
