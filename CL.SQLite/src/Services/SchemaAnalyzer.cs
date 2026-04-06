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

    /// <summary>
    /// Initializes a new instance of <see cref="SchemaAnalyzer"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public SchemaAnalyzer(ILogger? logger = null)
    {
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reflects over the given model type and returns a column definition for each
    /// property decorated with <see cref="SQLiteColumnAttribute"/>.
    /// </summary>
    /// <param name="modelType">The entity type to inspect.</param>
    /// <returns>An ordered list of <see cref="ModelColumnDefinition"/> objects.</returns>
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

    /// <summary>
    /// Generates a <c>CREATE TABLE IF NOT EXISTS</c> statement for the given table name and column definitions.
    /// </summary>
    /// <param name="tableName">The target table name.</param>
    /// <param name="columns">The column definitions to include in the table.</param>
    /// <returns>A complete SQL CREATE TABLE statement string.</returns>
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

    /// <summary>
    /// Returns the list of existing column names in the specified table by querying <c>PRAGMA table_info</c>.
    /// </summary>
    /// <param name="conn">An open SQLite connection.</param>
    /// <param name="tableName">The table to inspect.</param>
    /// <returns>A list of column name strings as stored in the database.</returns>
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

    /// <summary>
    /// Determines whether a table with the given name exists in the database.
    /// </summary>
    /// <param name="conn">An open SQLite connection.</param>
    /// <param name="tableName">The table name to check for.</param>
    /// <returns><c>true</c> if the table exists; otherwise <c>false</c>.</returns>
    public async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is not null && result is not DBNull;
    }

    /// <summary>
    /// Maps a <see cref="SQLiteDataType"/> enum value to the corresponding SQLite type affinity string.
    /// </summary>
    /// <param name="dt">The data type to map.</param>
    /// <returns>The SQLite type affinity string (e.g. <c>"INTEGER"</c>, <c>"TEXT"</c>).</returns>
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

    /// <summary>
    /// Returns the SQLite table name for the given entity type, using <see cref="SQLiteTableAttribute"/> when present,
    /// or the type's simple name as a fallback.
    /// </summary>
    /// <param name="entityType">The entity type to resolve the table name for.</param>
    /// <returns>The table name string.</returns>
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

    /// <summary>
    /// Describes the schema of a single entity property as it maps to a SQLite column.
    /// </summary>
    public sealed class ModelColumnDefinition
    {
        /// <summary>Gets or sets the C# property name on the entity class.</summary>
        public string PropertyName { get; set; } = string.Empty;

        /// <summary>Gets or sets the SQLite column name.</summary>
        public string ColumnName { get; set; } = string.Empty;

        /// <summary>Gets or sets the SQLite data type affinity for the column.</summary>
        public SQLiteDataType DataType { get; set; }

        /// <summary>Gets or sets a value indicating whether this column is part of the primary key.</summary>
        public bool IsPrimaryKey { get; set; }

        /// <summary>Gets or sets a value indicating whether the primary key column uses AUTOINCREMENT.</summary>
        public bool IsAutoIncrement { get; set; }

        /// <summary>Gets or sets a value indicating whether the column has a NOT NULL constraint.</summary>
        public bool IsNotNull { get; set; }

        /// <summary>Gets or sets a value indicating whether the column has a UNIQUE constraint.</summary>
        public bool IsUnique { get; set; }

        /// <summary>Gets or sets the SQL literal used as the column's DEFAULT value, or <c>null</c> for no default.</summary>
        public string? DefaultValue { get; set; }

        /// <summary>Gets or sets the foreign key attribute declaring a REFERENCES constraint, or <c>null</c> if none.</summary>
        public SQLiteForeignKeyAttribute? ForeignKey { get; set; }

        /// <summary>Gets or sets the reflected CLR <see cref="Type"/> of the property.</summary>
        public Type? PropertyType { get; set; }
    }
}
