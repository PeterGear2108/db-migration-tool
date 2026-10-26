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

        const string indexQuery = @"
            SELECT i.name AS IndexName, i.is_unique, c.name AS ColumnName, i.filter_definition
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@TableName) AND i.is_primary_key = 0 AND i.type > 0
            ORDER BY i.name, ic.key_ordinal";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = indexQuery;
            AddParam(command, "@TableName", tableName);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var indexName = reader.GetString(0);
                var isUnique = reader.GetBoolean(1);
                var columnName = reader.GetString(2);
                var filterDefinition = reader.IsDBNull(3) ? null : reader.GetString(3);

                var index = schema.Indexes.FirstOrDefault(i => i.IndexName == indexName);
                if (index is null)
                {
                    index = new IndexInfo { IndexName = indexName, IsUnique = isUnique, FilterDefinition = filterDefinition };
                    schema.Indexes.Add(index);
                }

                index.Columns.Add(columnName);
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
        var pk = sourceSchema.PrimaryKeyColumns;

        var columnDefs = sourceSchema.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c =>
            {
                var identity = c.IsIdentity && pk.Contains(c.ColumnName) ? " IDENTITY(1,1)" : "";
                return $"{EscapeIdentifier(c.ColumnName)} {MapType(c)} {(c.IsNullable ? "NULL" : "NOT NULL")}{identity}";
            });

        var sql = $"CREATE TABLE {EscapeIdentifier(sourceSchema.TableName)} (\n  " +
                   string.Join(",\n  ", columnDefs);

        if (pk.Count > 0)
        {
            sql += $",\n  CONSTRAINT {EscapeIdentifier("PK_" + sourceSchema.TableName)} PRIMARY KEY (" +
                   string.Join(", ", pk.Select(EscapeIdentifier)) + ")";
        }

        sql += "\n)";
        return sql;
    }

    public List<string> GenerateCreateIndexDdl(TableSchema sourceSchema)
    {
        return sourceSchema.Indexes.Select(ix =>
        {
            var unique = ix.IsUnique ? "UNIQUE " : "";
            var columns = string.Join(", ", ix.Columns.Select(EscapeIdentifier));
            var filter = string.IsNullOrWhiteSpace(ix.FilterDefinition) ? "" : $" WHERE {ix.FilterDefinition}";
            return $"CREATE {unique}INDEX {EscapeIdentifier(ix.IndexName)} ON {EscapeIdentifier(sourceSchema.TableName)} ({columns}){filter}";
        }).ToList();
    }

    public bool SupportsDestructiveReload => true;

    private List<ForeignKeyDefinition> _droppedForeignKeys = new();

    public async Task DropForeignKeysAsync(DbConnection connection)
    {
        const string fkQuery = @"
            SELECT
                fk.name AS ConstraintName,
                OBJECT_NAME(fk.parent_object_id) AS TableName,
                COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS ColumnName,
                OBJECT_NAME(fk.referenced_object_id) AS ReferencedTableName,
                COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS ReferencedColumnName,
                fk.delete_referential_action_desc AS DeleteAction,
                fk.update_referential_action_desc AS UpdateAction
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            ORDER BY fk.name, fkc.constraint_column_id";

        var definitions = new List<ForeignKeyDefinition>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = fkQuery;
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var constraintName = reader.GetString(0);
                var tableName = reader.GetString(1);
                var columnName = reader.GetString(2);
                var referencedTableName = reader.GetString(3);
                var referencedColumnName = reader.GetString(4);
                var deleteAction = reader.GetString(5).Replace("_", " ");
                var updateAction = reader.GetString(6).Replace("_", " ");

                var fk = definitions.FirstOrDefault(f => f.ConstraintName == constraintName && f.TableName == tableName);
                if (fk is null)
                {
                    fk = new ForeignKeyDefinition
                    {
                        ConstraintName = constraintName,
                        TableName = tableName,
                        ReferencedTableName = referencedTableName,
                        DeleteAction = deleteAction,
                        UpdateAction = updateAction
                    };
                    definitions.Add(fk);
                }

                fk.Columns.Add(columnName);
                fk.ReferencedColumns.Add(referencedColumnName);
            }
        }

        foreach (var fk in definitions)
        {
            using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = $"ALTER TABLE {EscapeIdentifier(fk.TableName)} DROP CONSTRAINT {EscapeIdentifier(fk.ConstraintName)}";
            await dropCmd.ExecuteNonQueryAsync();
        }

        _droppedForeignKeys = definitions;
    }

    public async Task RecreateForeignKeysAsync(DbConnection connection)
    {
        foreach (var fk in _droppedForeignKeys)
        {
            var columns = string.Join(", ", fk.Columns.Select(EscapeIdentifier));
            var refColumns = string.Join(", ", fk.ReferencedColumns.Select(EscapeIdentifier));

            using var addCmd = connection.CreateCommand();
            addCmd.CommandText =
                $"ALTER TABLE {EscapeIdentifier(fk.TableName)} ADD CONSTRAINT {EscapeIdentifier(fk.ConstraintName)} " +
                $"FOREIGN KEY ({columns}) REFERENCES {EscapeIdentifier(fk.ReferencedTableName)} ({refColumns}) " +
                $"ON DELETE {fk.DeleteAction} ON UPDATE {fk.UpdateAction}";
            await addCmd.ExecuteNonQueryAsync();
        }

        _droppedForeignKeys = new();
    }

    public async Task DropSecondaryIndexesAsync(DbConnection connection, string tableName)
    {
        const string indexQuery = @"
            SELECT i.name AS IndexName, i.is_unique_constraint
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(@TableName) AND i.is_primary_key = 0 AND i.type > 0";

        var indexes = new List<(string Name, bool IsUniqueConstraint)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = indexQuery;
            AddParam(command, "@TableName", tableName);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexes.Add((reader.GetString(0), reader.GetBoolean(1)));
            }
        }

        foreach (var (name, isUniqueConstraint) in indexes)
        {
            using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = isUniqueConstraint
                ? $"ALTER TABLE {EscapeIdentifier(tableName)} DROP CONSTRAINT {EscapeIdentifier(name)}"
                : $"DROP INDEX {EscapeIdentifier(name)} ON {EscapeIdentifier(tableName)}";
            await dropCmd.ExecuteNonQueryAsync();
        }
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
