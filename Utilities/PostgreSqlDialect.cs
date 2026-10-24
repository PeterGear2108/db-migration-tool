using System.Data;
using System.Data.Common;
using Npgsql;
using DBMigrationTool.Models;

namespace DBMigrationTool.Utilities;

public class PostgreSqlDialect : IDatabaseDialect
{
    public DatabaseEngine Engine => DatabaseEngine.PostgreSql;

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public string EscapeIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public async Task<List<string>> GetTableNamesAsync(DbConnection connection)
    {
        var tables = new List<string>();
        const string query = @"
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            ORDER BY table_name";

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
                c.column_name,
                c.data_type,
                c.ordinal_position,
                CASE WHEN c.is_nullable = 'YES' THEN 1 ELSE 0 END AS is_nullable,
                CASE WHEN c.column_default LIKE 'nextval(%' OR c.is_identity = 'YES' THEN 1 ELSE 0 END AS is_identity,
                c.character_maximum_length,
                c.numeric_precision,
                c.numeric_scale,
                CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS is_pk
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT ku.table_name, ku.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage ku
                    ON tc.constraint_name = ku.constraint_name AND tc.table_schema = ku.table_schema
                WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = 'public'
            ) pk ON pk.table_name = c.table_name AND pk.column_name = c.column_name
            WHERE c.table_schema = 'public' AND c.table_name = @TableName
            ORDER BY c.ordinal_position";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = columnsQuery;
            AddParam(command, "TableName", tableName);
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
                    MaxLength = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    NumericPrecision = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    NumericScale = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    IsPrimaryKey = reader.GetInt32(8) == 1
                });
            }
        }

        const string fkQuery = @"
            SELECT
                tc.constraint_name,
                kcu.column_name,
                ccu.table_name AS referenced_table,
                ccu.column_name AS referenced_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
                ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public' AND tc.table_name = @TableName";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = fkQuery;
            AddParam(command, "TableName", tableName);
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
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @TableName";
        AddParam(command, "TableName", tableName);
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
                var isSerial = c.IsIdentity && pk.Contains(c.ColumnName);
                var type = isSerial ? MapToSerial(c.DataType) : MapType(c);
                return $"{EscapeIdentifier(c.ColumnName)} {type} {(c.IsNullable ? "NULL" : "NOT NULL")}";
            });

        var sql = $"CREATE TABLE {EscapeIdentifier(sourceSchema.TableName)} (\n  " +
                   string.Join(",\n  ", columnDefs);

        if (pk.Count > 0)
        {
            sql += $",\n  CONSTRAINT {EscapeIdentifier("pk_" + sourceSchema.TableName)} PRIMARY KEY (" +
                   string.Join(", ", pk.Select(EscapeIdentifier)) + ")";
        }

        sql += "\n)";
        return sql;
    }

    private static string MapToSerial(string sourceType) => sourceType.ToLower() switch
    {
        "bigint" or "int8" => "BIGSERIAL",
        "smallint" or "int2" => "SMALLSERIAL",
        _ => "SERIAL"
    };

    private static string MapType(ColumnSchema c)
    {
        var type = c.DataType.ToLower();
        return type switch
        {
            "int" or "integer" or "int4" => "INTEGER",
            "bigint" or "int8" => "BIGINT",
            "smallint" or "int2" => "SMALLINT",
            "tinyint" => "SMALLINT",
            "bit" or "boolean" or "bool" => "BOOLEAN",
            "varchar" or "character varying" or "nvarchar" =>
                $"VARCHAR({(c.MaxLength is null or <= 0 ? 255 : c.MaxLength)})",
            "char" or "nchar" or "character" =>
                $"CHAR({(c.MaxLength is null or <= 0 ? 1 : c.MaxLength)})",
            "text" or "ntext" => "TEXT",
            "decimal" or "numeric" or "money" => $"NUMERIC({c.NumericPrecision ?? 18},{c.NumericScale ?? 2})",
            "float" or "real" or "double precision" => "DOUBLE PRECISION",
            "datetime" or "datetime2" or "smalldatetime" or "timestamp" or "timestamp without time zone" => "TIMESTAMP",
            "datetimeoffset" or "timestamp with time zone" or "timestamptz" => "TIMESTAMPTZ",
            "date" => "DATE",
            "time" => "TIME",
            "uniqueidentifier" or "uuid" => "UUID",
            "binary" or "varbinary" or "image" or "bytea" => "BYTEA",
            "json" or "jsonb" => "JSONB",
            _ => "TEXT"
        };
    }

    public async Task TruncateOrDeleteAsync(DbConnection connection, string tableName, ILoggerAdapter logger)
    {
        try
        {
            using var truncateCmd = connection.CreateCommand();
            truncateCmd.CommandText = $"TRUNCATE TABLE {EscapeIdentifier(tableName)} RESTART IDENTITY CASCADE";
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

    public Task SetConstraintCheckingAsync(DbConnection connection, string tableName, bool enabled)
    {
        // Postgres no permite deshabilitar FKs por tabla de forma simple sin ser superuser;
        // confiamos en TRUNCATE ... CASCADE y en el orden de dependencias.
        return Task.CompletedTask;
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
        var npgsqlConnection = (NpgsqlConnection)connection;

        var columnNames = data.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        var copyCommand = $"COPY {EscapeIdentifier(tableName)} ({string.Join(", ", columnNames.Select(EscapeIdentifier))}) FROM STDIN (FORMAT BINARY)";

        await using var writer = await npgsqlConnection.BeginBinaryImportAsync(copyCommand);

        foreach (DataRow row in data.Rows)
        {
            await writer.StartRowAsync();
            foreach (DataColumn column in data.Columns)
            {
                var value = row[column];
                if (value == DBNull.Value || value is null)
                {
                    await writer.WriteNullAsync();
                }
                else
                {
                    await writer.WriteAsync(value);
                }
            }
        }

        await writer.CompleteAsync();
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
