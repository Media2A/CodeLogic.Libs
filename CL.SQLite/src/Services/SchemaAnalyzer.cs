using System.Reflection;
using System.Text;
using CL.SQLite.Models;
using CodeLogic.Core.Logging;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Analyzes C# entity types and SQLite database schemas to generate CREATE TABLE
/// and ALTER TABLE statements for table synchronization.
/// </summary>
internal sealed class SchemaAnalyzer
{
    private readonly ILogger? _logger;

    public SchemaAnalyzer(ILogger? logger = null)
    {
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public List<ModelColumnDefinition> GetModelColumns(Type modelType)
    {
        var result = new List<ModelColumnDefinition>();
        foreach (var prop in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
            if (colAttr is null) continue;

            var fkAttr = prop.GetCustomAttribute<SQLiteForeignKeyAttribute>();
            result.Add(new ModelColumnDefinition
            {
                PropertyName = prop.Name,
                ColumnName   = colAttr.ColumnName ?? prop.Name,
                DataType     = colAttr.DataType,
                IsPrimaryKey       = colAttr.IsPrimaryKey,
                IsAutoIncrement    = colAttr.IsAutoIncrement,
                IsNotNull          = colAttr.IsNotNull || colAttr.IsPrimaryKey,
                IsUnique           = colAttr.IsUnique,
                DefaultValue       = colAttr.DefaultValue,
                ForeignKey         = fkAttr,
                PropertyType       = prop.PropertyType
            });
        }
        return result;
    }

    public string GenerateCreateTableSql(string tableName, List<ModelColumnDefinition> columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE IF NOT EXISTS \"{tableName}\" (");

        var defs = new List<string>();

        foreach (var col in columns)
        {
            var typeName = MapDataType(col.DataType);
            var sb2 = new StringBuilder($"  \"{col.ColumnName}\" {typeName}");

            if (col.IsPrimaryKey && col.IsAutoIncrement)
                sb2.Append(" PRIMARY KEY AUTOINCREMENT");
            else if (col.IsPrimaryKey)
                sb2.Append(" PRIMARY KEY");

            if (col.IsNotNull && !col.IsPrimaryKey)
                sb2.Append(" NOT NULL");

            if (col.IsUnique && !col.IsPrimaryKey)
                sb2.Append(" UNIQUE");

            if (col.DefaultValue is not null)
                sb2.Append($" DEFAULT {col.DefaultValue}");

            defs.Add(sb2.ToString());
        }

        // Foreign key constraints
        foreach (var col in columns.Where(c => c.ForeignKey is not null))
        {
            var fk = col.ForeignKey!;
            var onDelete = MapForeignKeyAction(fk.OnDelete);
            var onUpdate = MapForeignKeyAction(fk.OnUpdate);
            defs.Add($"  FOREIGN KEY (\"{col.ColumnName}\") REFERENCES \"{fk.ReferencedTable}\" (\"{fk.ReferencedColumn}\") ON DELETE {onDelete} ON UPDATE {onUpdate}");
        }

        sb.Append(string.Join(",\n", defs));
        sb.AppendLine();
        sb.Append(");");

        return sb.ToString();
    }

    public async Task<List<string>> GetDatabaseColumnsAsync(SqliteConnection conn, string tableName)
    {
        var columns = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1)); // column name is at index 1
        }
        return columns;
    }

    public async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is not null && result is not DBNull;
    }

    public string MapDataType(SQLiteDataType dt) => dt switch
    {
        SQLiteDataType.INTEGER  => "INTEGER",
        SQLiteDataType.REAL     => "REAL",
        SQLiteDataType.TEXT     => "TEXT",
        SQLiteDataType.BLOB     => "BLOB",
        SQLiteDataType.NUMERIC  => "NUMERIC",
        SQLiteDataType.DATETIME => "TEXT",
        SQLiteDataType.DATE     => "TEXT",
        SQLiteDataType.BOOLEAN  => "INTEGER",
        SQLiteDataType.UUID     => "TEXT",
        _                       => "TEXT"
    };

    public static string GetTableName(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<SQLiteTableAttribute>();
        return attr?.TableName ?? entityType.Name;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string MapForeignKeyAction(ForeignKeyAction action) => action switch
    {
        ForeignKeyAction.Cascade   => "CASCADE",
        ForeignKeyAction.Restrict  => "RESTRICT",
        ForeignKeyAction.SetNull   => "SET NULL",
        ForeignKeyAction.SetDefault => "SET DEFAULT",
        _                          => "NO ACTION"
    };

    // ── Inner class ──────────────────────────────────────────────────────────

    public sealed class ModelColumnDefinition
    {
        public string PropertyName { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
        public SQLiteDataType DataType { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsAutoIncrement { get; set; }
        public bool IsNotNull { get; set; }
        public bool IsUnique { get; set; }
        public string? DefaultValue { get; set; }
        public SQLiteForeignKeyAttribute? ForeignKey { get; set; }
        public Type? PropertyType { get; set; }
    }
}
