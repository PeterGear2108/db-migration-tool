using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using DBMigrationTool.Models;

namespace DBMigrationTool.Utilities;

public class SqlServerDialect : IDatabaseDialect
{
    public DatabaseEngine Engine => DatabaseEngine.SqlServer;

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public string EscapeIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public async Task<List<string>> GetTableNamesAsync(DbConnection connection)
    {
        var tables = new List<string>();
        const string query = @"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
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
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.ORDINAL_POSITION,
                CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS IS_NULLABLE,
                COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsIdentity') AS IS_IDENTITY,
                c.CHARACTER_MAXIMUM_LENGTH,
                c.NUMERIC_PRECISION,
                c.NUMERIC_SCALE,
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IS_PK
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
            WHERE c.TABLE_NAME = @TableName
            ORDER BY c.ORDINAL_POSITION";

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
                    IsIdentity = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
                    MaxLength = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    NumericPrecision = reader.IsDBNull(6) ? null : (int)reader.GetByte(6),
                    NumericScale = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    IsPrimaryKey = reader.GetInt32(8) == 1
                });
            }
        }

        const string fkQuery = @"
            SELECT
                fk.name AS ConstraintName,
                COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS ColumnName,
                OBJECT_NAME(fkc.referenced_object_id) AS ReferencedTable,
                COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS ReferencedColumn
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            WHERE OBJECT_NAME(fkc.parent_object_id) = @TableName";

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
        command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @TableName";
        AddParam(command, "@TableName", tableName);
        var count = (int)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    public string GenerateCreateTableDdl(TableSchema sourceSchema)
    {
        var columnDefs = sourceSchema.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => $"{EscapeIdentifier(c.ColumnName)} {MapType(c)} {(c.IsNullable ? "NULL" : "NOT NULL")}");

        var sql = $"CREATE TABLE {EscapeIdentifier(sourceSchema.TableName)} (\n  " +
                   string.Join(",\n  ", columnDefs);

        var pk = sourceSchema.PrimaryKeyColumns;
        if (pk.Count > 0)
        {
            sql += $",\n  CONSTRAINT {EscapeIdentifier("PK_" + sourceSchema.TableName)} PRIMARY KEY (" +
                   string.Join(", ", pk.Select(EscapeIdentifier)) + ")";
        }

        sql += "\n)";
        return sql;
    }

    private static string MapType(ColumnSchema c)
    {
        var type = c.DataType.ToLower();
        return type switch
        {
            "varchar" or "nvarchar" or "char" or "nchar" =>
                $"{type}({(c.MaxLength is null or < 0 ? "MAX" : c.MaxLength.ToString())})",
            "decimal" or "numeric" =>
                $"{type}({c.NumericPrecision ?? 18},{c.NumericScale ?? 2})",
            _ => type
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
        command.CommandText = enabled
            ? $"ALTER TABLE {EscapeIdentifier(tableName)} WITH CHECK CHECK CONSTRAINT ALL"
            : $"ALTER TABLE {EscapeIdentifier(tableName)} NOCHECK CONSTRAINT ALL";
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> CountRowsAsync(DbConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {EscapeIdentifier(tableName)}";
        return (int)(await command.ExecuteScalarAsync())!;
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
        var sqlConnection = (SqlConnection)connection;
        var sqlTransaction = (SqlTransaction)transaction;

        if (hasIdentityColumn && enableIdentityInsert)
        {
            using var enableCmd = new SqlCommand(
                $"SET IDENTITY_INSERT {EscapeIdentifier(tableName)} ON", sqlConnection, sqlTransaction);
            await enableCmd.ExecuteNonQueryAsync();
        }

        using (var bulkCopy = new SqlBulkCopy(sqlConnection, SqlBulkCopyOptions.KeepIdentity, sqlTransaction)
        {
            DestinationTableName = tableName,
            BatchSize = 1000,
            BulkCopyTimeout = 600
        })
        {
            foreach (DataColumn column in data.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(data);
        }

        if (hasIdentityColumn && enableIdentityInsert)
        {
            using var disableCmd = new SqlCommand(
                $"SET IDENTITY_INSERT {EscapeIdentifier(tableName)} OFF", sqlConnection, sqlTransaction);
            await disableCmd.ExecuteNonQueryAsync();
        }

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
