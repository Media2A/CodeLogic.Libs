using System.Reflection;
using System.Text;
using CL.MySQL2.Models;
using CodeLogic.Core.Logging;
using MySqlConnector;

namespace CL.MySQL2.Core;

/// <summary>
/// Analyzes entity types and generates the DDL statements required to create or alter
/// the corresponding MySQL table so it matches the current class definition.
/// </summary>
internal sealed class SchemaAnalyzer
{
    private readonly ILogger? _logger;

    public SchemaAnalyzer(ILogger? logger = null)
    {
        _logger = logger;
    }

    // ── Table name ────────────────────────────────────────────────────────────

    /// <summary>Returns the table name for the given entity type.</summary>
    public static string GetTableName(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : entityType.Name;
    }

    // ── CREATE TABLE ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a CREATE TABLE IF NOT EXISTS statement for the given entity type.
    /// </summary>
    public string GenerateCreateTable(Type entityType)
    {
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>() ?? new TableAttribute();
        var tableName = !string.IsNullOrEmpty(tableAttr.Name) ? tableAttr.Name! : entityType.Name;

        var sql = new StringBuilder();
        sql.AppendLine($"CREATE TABLE IF NOT EXISTS `{tableName}` (");

        var columns = new List<string>();
        var primaryKeys = new List<string>();
        var indexes = new List<string>();
        var uniqueIndexes = new List<string>();
        var foreignKeys = new List<string>();

        var properties = GetMappedProperties(entityType);

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;
            var colDef = BuildColumnDef(prop, colAttr, colName);
            columns.Add($"  {colDef}");

            if (colAttr?.Primary == true)
                primaryKeys.Add($"`{colName}`");

            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
                indexes.Add($"  INDEX `idx_{tableName}_{colName}` (`{colName}`)");

            if (colAttr?.Unique == true && colAttr.Primary == false)
                uniqueIndexes.Add($"  UNIQUE KEY `uq_{tableName}_{colName}` (`{colName}`)");

            // Foreign key
            var fkAttr = prop.GetCustomAttribute<ForeignKeyAttribute>();
            if (fkAttr is not null)
            {
                var constraintName = fkAttr.ConstraintName
                    ?? $"fk_{tableName}_{colName}_{fkAttr.ReferenceTable}";
                var onDelete = FkActionToSql(fkAttr.OnDelete);
                var onUpdate = FkActionToSql(fkAttr.OnUpdate);
                foreignKeys.Add(
                    $"  CONSTRAINT `{constraintName}` FOREIGN KEY (`{colName}`) " +
                    $"REFERENCES `{fkAttr.ReferenceTable}` (`{fkAttr.ReferenceColumn}`) " +
                    $"ON DELETE {onDelete} ON UPDATE {onUpdate}");
            }
        }

        // Composite indexes on class
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
        {
            var cols = string.Join(", ", ci.ColumnNames.Select(c => $"`{c}`"));
            if (ci.Unique)
                uniqueIndexes.Add($"  UNIQUE KEY `{ci.IndexName}` ({cols})");
            else
                indexes.Add($"  INDEX `{ci.IndexName}` ({cols})");
        }

        var allDefs = columns.ToList();
        if (primaryKeys.Count > 0)
            allDefs.Add($"  PRIMARY KEY ({string.Join(", ", primaryKeys)})");
        allDefs.AddRange(indexes);
        allDefs.AddRange(uniqueIndexes);
        allDefs.AddRange(foreignKeys);

        sql.Append(string.Join(",\n", allDefs));
        sql.AppendLine();
        sql.Append(')');

        // Table options
        var engineName = tableAttr.Engine.ToString().ToUpperInvariant();
        var charsetName = CharsetToString(tableAttr.Charset);
        sql.Append($" ENGINE={engineName} DEFAULT CHARSET={charsetName}");

        if (!string.IsNullOrEmpty(tableAttr.Collation))
            sql.Append($" COLLATE={tableAttr.Collation}");

        if (!string.IsNullOrEmpty(tableAttr.Comment))
            sql.Append($" COMMENT='{EscapeString(tableAttr.Comment)}'");

        sql.Append(';');
        return sql.ToString();
    }

    // ── Schema diff ────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares the entity type definition against the live database schema and returns
    /// a list of ALTER TABLE statements needed to bring the table in sync.
    /// </summary>
    public async Task<List<string>> GenerateAlterStatementsAsync(
        Type entityType,
        MySqlConnection connection,
        CancellationToken ct = default)
    {
        var tableName = GetTableName(entityType);
        var alterStatements = new List<string>();

        // Load current columns from INFORMATION_SCHEMA
        var existingColumns = await GetExistingColumnsAsync(connection, tableName, ct)
            .ConfigureAwait(false);

        var existingIndexes = await GetExistingIndexesAsync(connection, tableName, ct)
            .ConfigureAwait(false);

        var properties = GetMappedProperties(entityType);

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;

            if (!existingColumns.ContainsKey(colName))
            {
                // Column missing — ADD COLUMN
                var colDef = BuildColumnDef(prop, colAttr, colName);
                alterStatements.Add($"ALTER TABLE `{tableName}` ADD COLUMN {colDef};");
                _logger?.Debug($"[MySQL2] Will add column `{tableName}`.`{colName}`");
            }
            else
            {
                // Column exists — check if MODIFY is needed
                var existing = existingColumns[colName];
                var modifyDef = BuildColumnDef(prop, colAttr, colName);

                if (ColumnNeedsModify(existing, prop, colAttr))
                {
                    alterStatements.Add($"ALTER TABLE `{tableName}` MODIFY COLUMN {modifyDef};");
                    _logger?.Debug($"[MySQL2] Will modify column `{tableName}`.`{colName}`");
                }
            }

            // Index management
            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
            {
                var idxName = $"idx_{tableName}_{colName}";
                if (!existingIndexes.ContainsKey(idxName))
                    alterStatements.Add($"ALTER TABLE `{tableName}` ADD INDEX `{idxName}` (`{colName}`);");
            }

            if (colAttr?.Unique == true && colAttr.Primary == false)
            {
                var idxName = $"uq_{tableName}_{colName}";
                if (!existingIndexes.ContainsKey(idxName))
                    alterStatements.Add($"ALTER TABLE `{tableName}` ADD UNIQUE KEY `{idxName}` (`{colName}`);");
            }
        }

        // Composite indexes
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
        {
            if (!existingIndexes.ContainsKey(ci.IndexName))
            {
                var cols = string.Join(", ", ci.ColumnNames.Select(c => $"`{c}`"));
                var keyword = ci.Unique ? "UNIQUE KEY" : "INDEX";
                alterStatements.Add($"ALTER TABLE `{tableName}` ADD {keyword} `{ci.IndexName}` ({cols});");
            }
        }

        return alterStatements;
    }

    // ── Column DDL builder ────────────────────────────────────────────────────

    private static string BuildColumnDef(PropertyInfo prop, ColumnAttribute? colAttr, string colName)
    {
        var sb = new StringBuilder();
        sb.Append($"`{colName}` ");

        var dataType = colAttr?.DataType ?? TypeConverter.InferDataType(prop.PropertyType);
        var fakeAttr = colAttr ?? new ColumnAttribute { DataType = dataType };

        if (colAttr is null)
            fakeAttr = new ColumnAttribute { DataType = dataType };
        else
            fakeAttr = colAttr;

        sb.Append(TypeConverter.GetMySqlType(fakeAttr));

        if (!string.IsNullOrEmpty(colAttr?.Charset))
            sb.Append($" CHARACTER SET {colAttr.Charset}");

        if (colAttr?.NotNull == true || colAttr?.Primary == true)
            sb.Append(" NOT NULL");
        else if (IsNullableType(prop.PropertyType))
            sb.Append(" NULL");

        if (colAttr?.AutoIncrement == true)
            sb.Append(" AUTO_INCREMENT");

        if (!string.IsNullOrEmpty(colAttr?.DefaultValue))
            sb.Append($" DEFAULT {colAttr.DefaultValue}");

        if (colAttr?.OnUpdateCurrentTimestamp == true)
            sb.Append(" ON UPDATE CURRENT_TIMESTAMP");

        if (!string.IsNullOrEmpty(colAttr?.Comment))
            sb.Append($" COMMENT '{EscapeString(colAttr.Comment)}'");

        return sb.ToString();
    }

    // ── INFORMATION_SCHEMA queries ────────────────────────────────────────────

    private static async Task<Dictionary<string, ColumnInfo>> GetExistingColumnsAsync(
        MySqlConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        var dbName = connection.Database;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
                   EXTRA, CHARACTER_SET_NAME, COLLATION_NAME, COLUMN_COMMENT
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl
            ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@db", dbName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            result[name] = new ColumnInfo(
                Name: name,
                ColumnType: reader.GetString(1),
                IsNullable: reader.GetString(2) == "YES",
                DefaultValue: reader.IsDBNull(3) ? null : reader.GetString(3),
                Extra: reader.GetString(4),
                CharacterSet: reader.IsDBNull(5) ? null : reader.GetString(5),
                Comment: reader.IsDBNull(7) ? null : reader.GetString(7));
        }

        return result;
    }

    private static async Task<Dictionary<string, IndexInfo>> GetExistingIndexesAsync(
        MySqlConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, IndexInfo>(StringComparer.OrdinalIgnoreCase);
        var dbName = connection.Database;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT INDEX_NAME, NON_UNIQUE
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl
            GROUP BY INDEX_NAME, NON_UNIQUE";
        cmd.Parameters.AddWithValue("@db", dbName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            result[name] = new IndexInfo(name, reader.GetInt32(1) == 0);
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ColumnNeedsModify(ColumnInfo existing, PropertyInfo prop, ColumnAttribute? colAttr)
    {
        // Basic heuristic: if expected nullable differs from actual, recommend MODIFY
        var expectNullable = IsNullableType(prop.PropertyType) && colAttr?.Primary != true && colAttr?.NotNull != true;
        return existing.IsNullable != expectNullable;
    }

    private static bool IsNullableType(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static PropertyInfo[] GetMappedProperties(Type entityType) =>
        entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreAttribute>() is null)
            .ToArray();

    private static string FkActionToSql(ForeignKeyAction action) => action switch
    {
        ForeignKeyAction.Cascade    => "CASCADE",
        ForeignKeyAction.SetNull    => "SET NULL",
        ForeignKeyAction.NoAction   => "NO ACTION",
        ForeignKeyAction.SetDefault => "SET DEFAULT",
        _                           => "RESTRICT"
    };

    private static string CharsetToString(Charset charset) => charset switch
    {
        Charset.Utf8mb4 => "utf8mb4",
        Charset.Utf8    => "utf8",
        Charset.Latin1  => "latin1",
        Charset.Ascii   => "ascii",
        Charset.Binary  => "binary",
        _               => "utf8mb4"
    };

    private static string EscapeString(string s) => s.Replace("'", "''");

    // ── Internal record types ─────────────────────────────────────────────────

    private record ColumnInfo(
        string Name,
        string ColumnType,
        bool IsNullable,
        string? DefaultValue,
        string Extra,
        string? CharacterSet,
        string? Comment);

    private record IndexInfo(string Name, bool IsUnique);
}
