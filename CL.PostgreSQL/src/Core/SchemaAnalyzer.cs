using System.Reflection;
using System.Text;
using CL.PostgreSQL.Models;
using CodeLogic.Core.Logging;
using Npgsql;

namespace CL.PostgreSQL.Core;

/// <summary>
/// Analyzes entity types and generates PostgreSQL DDL statements to create or alter
/// the corresponding table so it matches the current class definition.
/// </summary>
internal sealed class SchemaAnalyzer
{
    private readonly ILogger? _logger;

    public SchemaAnalyzer(ILogger? logger = null)
    {
        _logger = logger;
    }

    // ── Table/Schema name helpers ─────────────────────────────────────────────

    public static string GetTableName(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : entityType.Name;
    }

    public static string GetSchemaName(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Schema) ? attr.Schema! : "public";
    }

    // ── CREATE TABLE ──────────────────────────────────────────────────────────

    public string GenerateCreateTableSql<T>(string schemaName = "public") where T : class
        => GenerateCreateTableSql(typeof(T), schemaName);

    public string GenerateCreateTableSql(Type entityType, string? schemaOverride = null)
    {
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>() ?? new TableAttribute();
        var tableName = !string.IsNullOrEmpty(tableAttr.Name) ? tableAttr.Name! : entityType.Name;
        var schemaName = schemaOverride ?? (!string.IsNullOrEmpty(tableAttr.Schema) ? tableAttr.Schema! : "public");

        var sql = new StringBuilder();
        sql.AppendLine($"CREATE TABLE IF NOT EXISTS \"{schemaName}\".\"{tableName}\" (");

        var columns = new List<string>();
        var primaryKeys = new List<string>();
        var indexes = new List<string>();
        var foreignKeys = new List<string>();

        var properties = GetMappedProperties(entityType);

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;
            var colDef = BuildColumnDefinitionFromProp(prop, colAttr, colName);
            columns.Add($"  {colDef}");

            if (colAttr?.Primary == true)
                primaryKeys.Add($"\"{colName}\"");

            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
                indexes.Add($"CREATE INDEX IF NOT EXISTS \"idx_{tableName}_{colName}\" ON \"{schemaName}\".\"{tableName}\" (\"{colName}\");");

            if (colAttr?.Unique == true && colAttr.Primary == false)
                columns[columns.Count - 1] += ""; // Unique handled in column def itself

            var fkAttr = prop.GetCustomAttribute<ForeignKeyAttribute>();
            if (fkAttr is not null)
            {
                var constraintName = fkAttr.ConstraintName ?? $"fk_{tableName}_{colName}_{fkAttr.ReferenceTable}";
                var onDelete = FkActionToSql(fkAttr.OnDelete);
                var onUpdate = FkActionToSql(fkAttr.OnUpdate);
                foreignKeys.Add(
                    $"  CONSTRAINT \"{constraintName}\" FOREIGN KEY (\"{colName}\") " +
                    $"REFERENCES \"{fkAttr.ReferenceTable}\" (\"{fkAttr.ReferenceColumn}\") " +
                    $"ON DELETE {onDelete} ON UPDATE {onUpdate}");
            }
        }

        // Unique constraints — add as separate lines
        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            if (colAttr?.Unique == true && colAttr.Primary == false)
            {
                var colName = !string.IsNullOrEmpty(colAttr.Name) ? colAttr.Name! : prop.Name;
                columns.Add($"  CONSTRAINT \"uq_{tableName}_{colName}\" UNIQUE (\"{colName}\")");
            }
        }

        if (primaryKeys.Count > 0)
            columns.Add($"  PRIMARY KEY ({string.Join(", ", primaryKeys)})");
        columns.AddRange(foreignKeys);

        sql.Append(string.Join(",\n", columns));
        sql.AppendLine();
        sql.Append(");");

        // Composite indexes
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
        {
            var cols = string.Join(", ", ci.ColumnNames.Select(c => $"\"{c}\""));
            var unique = ci.Unique ? "UNIQUE " : string.Empty;
            sql.AppendLine();
            sql.Append($"CREATE {unique}INDEX IF NOT EXISTS \"{ci.IndexName}\" ON \"{schemaName}\".\"{tableName}\" ({cols});");
        }

        // Individual column indexes
        foreach (var idx in indexes)
        {
            sql.AppendLine();
            sql.Append(idx);
        }

        return sql.ToString();
    }

    // ── Schema diff ────────────────────────────────────────────────────────────

    public async Task<List<string>> GenerateAlterStatementsAsync(
        Type entityType,
        NpgsqlConnection connection,
        CancellationToken ct = default)
    {
        var tableName = GetTableName(entityType);
        var schemaName = GetSchemaName(entityType);
        var alterStatements = new List<string>();

        var existingColumns = await GetColumnsAsync(connection, schemaName, tableName, ct).ConfigureAwait(false);
        var existingIndexes = await GetIndexesAsync(connection, schemaName, tableName, ct).ConfigureAwait(false);

        var existingColMap = existingColumns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
        var existingIdxMap = existingIndexes.ToDictionary(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase);

        var properties = GetMappedProperties(entityType);

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;

            if (!existingColMap.ContainsKey(colName))
            {
                var colDef = BuildColumnDefinitionFromProp(prop, colAttr, colName);
                alterStatements.Add($"ALTER TABLE \"{schemaName}\".\"{tableName}\" ADD COLUMN {colDef};");
                _logger?.Debug($"[PostgreSQL] Will add column \"{schemaName}\".\"{tableName}\".\"{colName}\"");
            }
            else
            {
                var existing = existingColMap[colName];
                var expectedType = GetPostgreSqlTypeFromProp(prop, colAttr);
                if (!string.Equals(NormalizeType(existing.DataType), NormalizeType(expectedType), StringComparison.OrdinalIgnoreCase))
                {
                    alterStatements.Add($"ALTER TABLE \"{schemaName}\".\"{tableName}\" ALTER COLUMN \"{colName}\" TYPE {expectedType};");
                    _logger?.Debug($"[PostgreSQL] Will alter column type \"{schemaName}\".\"{tableName}\".\"{colName}\"");
                }
            }

            // Index management
            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
            {
                var idxName = $"idx_{tableName}_{colName}";
                if (!existingIdxMap.ContainsKey(idxName))
                    alterStatements.Add($"CREATE INDEX IF NOT EXISTS \"{idxName}\" ON \"{schemaName}\".\"{tableName}\" (\"{colName}\");");
            }

            if (colAttr?.Unique == true && colAttr.Primary == false)
            {
                var idxName = $"uq_{tableName}_{colName}";
                if (!existingIdxMap.ContainsKey(idxName))
                    alterStatements.Add($"ALTER TABLE \"{schemaName}\".\"{tableName}\" ADD CONSTRAINT \"{idxName}\" UNIQUE (\"{colName}\");");
            }
        }

        // Composite indexes
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
        {
            if (!existingIdxMap.ContainsKey(ci.IndexName))
            {
                var cols = string.Join(", ", ci.ColumnNames.Select(c => $"\"{c}\""));
                var unique = ci.Unique ? "UNIQUE " : string.Empty;
                alterStatements.Add($"CREATE {unique}INDEX IF NOT EXISTS \"{ci.IndexName}\" ON \"{schemaName}\".\"{tableName}\" ({cols});");
            }
        }

        return alterStatements;
    }

    // ── Column definition ────────────────────────────────────────────────────

    public string GenerateColumnDefinition(ColumnDef col)
    {
        var sb = new StringBuilder();
        sb.Append($"\"{col.Name}\" ");

        var typeSql = TypeConverter.GetPostgreSqlType(col.DataType, col.Size, col.Precision, col.Scale);

        if (col.Primary && col.AutoIncrement)
        {
            sb.Append($"{typeSql} GENERATED ALWAYS AS IDENTITY PRIMARY KEY");
            return sb.ToString();
        }

        sb.Append(typeSql);

        if (col.NotNull || col.Primary)
            sb.Append(" NOT NULL");
        else if (IsNullableClrType(col.DataType))
            sb.Append(" NULL");

        if (col.Unique && !col.Primary)
            sb.Append(" UNIQUE");

        if (!string.IsNullOrEmpty(col.DefaultValue))
            sb.Append($" DEFAULT {col.DefaultValue}");

        if (!string.IsNullOrEmpty(col.Comment))
            sb.Append($" /* {col.Comment} */");

        return sb.ToString();
    }

    public string GetPostgreSqlType(DataType dt, int size, int precision, int scale)
        => TypeConverter.GetPostgreSqlType(dt, size, precision, scale);

    // ── INFORMATION_SCHEMA queries ────────────────────────────────────────────

    public async Task<List<ColumnInfo>> GetColumnsAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        CancellationToken ct = default)
    {
        var result = new List<ColumnInfo>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.column_name, c.data_type, c.is_nullable, c.column_default,
                   CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END AS is_pk,
                   pgd.description AS comment
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT ku.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage ku
                    ON tc.constraint_name = ku.constraint_name
                    AND tc.table_schema = ku.table_schema
                    AND tc.table_name = ku.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                    AND tc.table_schema = @schema
                    AND tc.table_name = @table
            ) pk ON c.column_name = pk.column_name
            LEFT JOIN pg_catalog.pg_statio_all_tables psat
                ON psat.schemaname = c.table_schema AND psat.relname = c.table_name
            LEFT JOIN pg_catalog.pg_description pgd
                ON pgd.objoid = psat.relid
                AND pgd.objsubid = c.ordinal_position
            WHERE c.table_schema = @schema AND c.table_name = @table
            ORDER BY c.ordinal_position";

        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ColumnInfo(
                Name: reader.GetString(0),
                DataType: reader.GetString(1),
                IsNullable: reader.GetString(2) == "YES",
                IsPrimaryKey: reader.GetBoolean(4),
                DefaultValue: reader.IsDBNull(3) ? null : reader.GetString(3),
                Comment: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }

    public async Task<bool> TableExistsAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table";
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return count > 0;
    }

    public async Task<List<IndexInfo>> GetIndexesAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        CancellationToken ct = default)
    {
        var result = new List<IndexInfo>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.relname AS index_name, ix.indisunique,
                   array_agg(a.attname ORDER BY array_position(ix.indkey, a.attnum)) AS columns
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
            WHERE n.nspname = @schema AND t.relname = @table
            GROUP BY i.relname, ix.indisunique";

        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var unique = reader.GetBoolean(1);
            var cols = reader.IsDBNull(2)
                ? new List<string>()
                : ((string[])reader.GetValue(2)).ToList();
            result.Add(new IndexInfo(name, unique, cols));
        }

        return result;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string BuildColumnDefinitionFromProp(PropertyInfo prop, ColumnAttribute? colAttr, string colName)
    {
        var sb = new StringBuilder();
        sb.Append($"\"{colName}\" ");

        var dataType = colAttr?.DataType ?? TypeConverter.InferDataType(prop.PropertyType);
        var size = colAttr?.Size ?? 0;
        var precision = colAttr?.Precision ?? 10;
        var scale = colAttr?.Scale ?? 2;
        var typeSql = TypeConverter.GetPostgreSqlType(dataType, size, precision, scale);

        if (colAttr?.Primary == true && colAttr.AutoIncrement == true)
        {
            sb.Append($"{typeSql} GENERATED ALWAYS AS IDENTITY PRIMARY KEY");
            return sb.ToString();
        }

        sb.Append(typeSql);

        if (colAttr?.NotNull == true || colAttr?.Primary == true)
            sb.Append(" NOT NULL");
        else if (IsNullableType(prop.PropertyType))
            sb.Append(" NULL");

        if (colAttr?.Unique == true && colAttr.Primary == false)
            sb.Append(" UNIQUE");

        if (!string.IsNullOrEmpty(colAttr?.DefaultValue))
            sb.Append($" DEFAULT {colAttr.DefaultValue}");

        if (!string.IsNullOrEmpty(colAttr?.Comment))
            sb.Append($" /* {colAttr.Comment.Replace("*/", "*\\/")} */");

        return sb.ToString();
    }

    private static string GetPostgreSqlTypeFromProp(PropertyInfo prop, ColumnAttribute? colAttr)
    {
        var dataType = colAttr?.DataType ?? TypeConverter.InferDataType(prop.PropertyType);
        return TypeConverter.GetPostgreSqlType(dataType, colAttr?.Size ?? 0, colAttr?.Precision ?? 10, colAttr?.Scale ?? 2);
    }

    private static string NormalizeType(string pgType)
        => pgType.ToLowerInvariant().Trim();

    private static bool IsNullableType(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static bool IsNullableClrType(DataType dt) =>
        dt is DataType.Text or DataType.VarChar or DataType.Char
            or DataType.Json or DataType.Jsonb or DataType.Bytea
            or DataType.TextArray;

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
}

public record ColumnDef(
    string Name,
    DataType DataType,
    int Size,
    int Precision,
    int Scale,
    bool Primary,
    bool AutoIncrement,
    bool NotNull,
    bool Unique,
    bool Index,
    string? DefaultValue,
    string? Comment);

public record ColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    string? DefaultValue,
    string? Comment);

public record IndexInfo(string Name, bool IsUnique, List<string> Columns);
