using System.Data;
using System.Data.Common;
using DBMigrationTool.Models;

namespace DBMigrationTool.Utilities;

/// <summary>
/// Encapsula todo lo que difiere entre motores de base de datos (SQL Server, PostgreSQL, MySQL):
/// creación de conexiones, introspección de esquema, DDL y operaciones masivas.
/// </summary>
public interface IDatabaseDialect
{
    DatabaseEngine Engine { get; }

    DbConnection CreateConnection(string connectionString);

    string EscapeIdentifier(string identifier);

    Task<List<string>> GetTableNamesAsync(DbConnection connection);

    Task<TableSchema> GetTableSchemaAsync(DbConnection connection, string tableName);

    Task<bool> TableExistsAsync(DbConnection connection, string tableName);

    /// <summary>Genera el DDL (CREATE TABLE) traduciendo tipos desde el esquema de origen.</summary>
    string GenerateCreateTableDdl(TableSchema sourceSchema);

    Task TruncateOrDeleteAsync(DbConnection connection, string tableName, ILoggerAdapter logger);

    Task SetConstraintCheckingAsync(DbConnection connection, string tableName, bool enabled);

    Task<int> CountRowsAsync(DbConnection connection, string tableName);

    string GenerateSelectStatement(string tableName, List<string> columns);

    /// <summary>Inserción masiva de un DataTable dentro de una transacción ya abierta.</summary>
    Task<int> BulkInsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        DataTable data,
        bool hasIdentityColumn,
        bool enableIdentityInsert);
}

/// <summary>Abstracción mínima de logging para no acoplar los dialectos a Microsoft.Extensions.Logging en cada llamada.</summary>
public interface ILoggerAdapter
{
    void Info(string message);
    void Warn(string message);
}
