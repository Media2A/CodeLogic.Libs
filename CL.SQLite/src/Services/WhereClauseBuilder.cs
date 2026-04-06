using CL.SQLite.Models;

namespace CL.SQLite.Services;

/// <summary>
/// Builds SQL WHERE clauses from a list of <see cref="WhereCondition"/> objects,
/// using indexed parameter names (@p0, @p1, ...).
/// </summary>
internal static class WhereClauseBuilder
{
    /// <summary>
    /// Builds a SQL WHERE clause string and its associated parameter dictionary from a list of conditions.
    /// Conditions are joined with their respective <see cref="WhereCondition.LogicalOperator"/> values.
    /// <c>NULL</c> values are converted to <c>IS NULL</c> / <c>IS NOT NULL</c> fragments automatically.
    /// </summary>
    /// <param name="conditions">The list of <see cref="WhereCondition"/> objects to combine.</param>
    /// <returns>
    /// A tuple containing the SQL WHERE clause (e.g. <c>" WHERE "col" = @p0"</c>) and
    /// a dictionary of named parameter bindings. Returns an empty clause and empty dictionary when
    /// <paramref name="conditions"/> is empty.
    /// </returns>
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
