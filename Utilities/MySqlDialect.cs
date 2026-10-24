using System.Data;
using System.Data.Common;
using MySqlConnector;
using DBMigrationTool.Models;

namespace DBMigrationTool.Utilities;

public class MySqlDialect : IDatabaseDialect
{
    public DatabaseEngine Engine => DatabaseEngine.MySql;

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public string EscapeIdentifier(string identifier) => $"`{identifier.Replace("`", "``")}`";

    public async Task<List<string>> GetTableNamesAsync(DbConnection connection)
    {
        var tables = new List<string>();
        const string query = @"
            SELECT TABLE_NAME FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME";

        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async Task<TableSchema> GetTableSchemaAsync(DbConnection connection, string tableName)
    {
        var schema = new TableSchema { TableName = tableName };

        const string columnsQuery = @"
            SELECT
                COLUMN_NAME,
                DATA_TYPE,
                ORDINAL_POSITION,
                CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS IS_NULLABLE,
                CASE WHEN EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END AS IS_IDENTITY,
                CHARACTER_MAXIMUM_LENGTH,
                NUMERIC_PRECISION,
                NUMERIC_SCALE,
                CASE WHEN COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END AS IS_PK
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName
            ORDER BY ORDINAL_POSITION";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = columnsQuery;
            AddParam(command, "@TableName", tableName);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schema.Columns.Add(new ColumnSchema
                {
                    ColumnName = reader.GetString(0),
                    DataType = reader.GetString(1),
                    OrdinalPosition = reader.GetInt32(2),
                    IsNullable = reader.GetInt32(3) == 1,
                    IsIdentity = reader.GetInt32(4) == 1,
                    MaxLength = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    NumericPrecision = reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6)),
                    NumericScale = reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7)),
                    IsPrimaryKey = reader.GetInt32(8) == 1
                });
            }
        }

        const string fkQuery = @"
            SELECT
                CONSTRAINT_NAME,
                COLUMN_NAME,
                REFERENCED_TABLE_NAME,
                REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName
                AND REFERENCED_TABLE_NAME IS NOT NULL";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = fkQuery;
            AddParam(command, "@TableName", tableName);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schema.ForeignKeys.Add(new ForeignKeyInfo
                {
                    ConstraintName = reader.GetString(0),
                    ColumnName = reader.GetString(1),
                    ReferencedTableName = reader.GetString(2),
                    ReferencedColumnName = reader.GetString(3)
                });
            }
        }

        return schema;
    }

    public async Task<bool> TableExistsAsync(DbConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName";
        AddParam(command, "@TableName", tableName);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        return count > 0;
    }

    public string GenerateCreateTableDdl(TableSchema sourceSchema)
    {
        var pk = sourceSchema.PrimaryKeyColumns;

        var columnDefs = sourceSchema.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c =>
            {
                var autoIncrement = c.IsIdentity && pk.Contains(c.ColumnName) ? " AUTO_INCREMENT" : "";
                return $"{EscapeIdentifier(c.ColumnName)} {MapType(c)} {(c.IsNullable ? "NULL" : "NOT NULL")}{autoIncrement}";
            });

        var sql = $"CREATE TABLE {EscapeIdentifier(sourceSchema.TableName)} (\n  " +
                   string.Join(",\n  ", columnDefs);

        if (pk.Count > 0)
        {
            sql += $",\n  PRIMARY KEY (" + string.Join(", ", pk.Select(EscapeIdentifier)) + ")";
        }

        sql += "\n) ENGINE=InnoDB";
        return sql;
    }

    private static string MapType(ColumnSchema c)
    {
        var type = c.DataType.ToLower();
        return type switch
        {
            "int" or "integer" => "INT",
            "bigint" => "BIGINT",
            "smallint" => "SMALLINT",
            "tinyint" or "bit" or "boolean" or "bool" => "TINYINT(1)",
            "varchar" or "nvarchar" or "character varying" =>
                $"VARCHAR({(c.MaxLength is null or <= 0 ? 255 : Math.Min(c.MaxLength.Value, 65535))})",
            "char" or "nchar" or "character" =>
                $"CHAR({(c.MaxLength is null or <= 0 ? 1 : c.MaxLength)})",
            "text" or "ntext" => "TEXT",
            "decimal" or "numeric" or "money" => $"DECIMAL({c.NumericPrecision ?? 18},{c.NumericScale ?? 2})",
            "float" or "real" => "FLOAT",
            "double precision" => "DOUBLE",
            "datetime" or "datetime2" or "smalldatetime" or "timestamp without time zone" => "DATETIME",
            "datetimeoffset" or "timestamp with time zone" or "timestamptz" or "timestamp" => "TIMESTAMP",
            "date" => "DATE",
            "time" => "TIME",
            "uniqueidentifier" or "uuid" => "CHAR(36)",
            "binary" or "varbinary" or "image" or "bytea" => "BLOB",
            "json" or "jsonb" => "JSON",
            _ => "TEXT"
        };
    }

    public async Task TruncateOrDeleteAsync(DbConnection connection, string tableName, ILoggerAdapter logger)
    {
        try
        {
            using var truncateCmd = connection.CreateCommand();
            truncateCmd.CommandText = $"TRUNCATE TABLE {EscapeIdentifier(tableName)}";
            await truncateCmd.ExecuteNonQueryAsync();
            logger.Info($"Tabla {tableName} limpiada (TRUNCATE)");
        }
        catch
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = $"DELETE FROM {EscapeIdentifier(tableName)}";
            await deleteCmd.ExecuteNonQueryAsync();
            logger.Info($"Tabla {tableName} limpiada (DELETE)");
        }
    }

    public async Task SetConstraintCheckingAsync(DbConnection connection, string tableName, bool enabled)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SET FOREIGN_KEY_CHECKS = {(enabled ? 1 : 0)}";
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> CountRowsAsync(DbConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {EscapeIdentifier(tableName)}";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public string GenerateSelectStatement(string tableName, List<string> columns)
    {
        var columnList = string.Join(", ", columns.Select(EscapeIdentifier));
        return $"SELECT {columnList} FROM {EscapeIdentifier(tableName)}";
    }

    public async Task<int> BulkInsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        DataTable data,
        bool hasIdentityColumn,
        bool enableIdentityInsert)
    {
        var mySqlConnection = (MySqlConnection)connection;
        var mySqlTransaction = (MySqlTransaction)transaction;

        var bulkCopy = new MySqlBulkCopy(mySqlConnection, mySqlTransaction)
        {
            DestinationTableName = tableName
        };

        foreach (DataColumn column in data.Columns)
        {
            bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(column.Ordinal, column.ColumnName));
        }

        await bulkCopy.WriteToServerAsync(data);
        return data.Rows.Count;
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
