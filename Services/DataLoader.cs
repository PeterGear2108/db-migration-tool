using System.Data;
using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface IDataLoader
{
    Task<int> LoadDataAsync(
        IDatabaseDialect dialect,
        string connectionString,
        string tableName,
        DataTable data,
        List<ColumnMapping> mappings,
        bool enableIdentityInsert);
}

public class DataLoader : IDataLoader
{
    private readonly ILogger<DataLoader> _logger;

    public DataLoader(ILogger<DataLoader> logger)
    {
        _logger = logger;
    }

    public async Task<int> LoadDataAsync(
        IDatabaseDialect dialect,
        string connectionString,
        string tableName,
        DataTable data,
        List<ColumnMapping> mappings,
        bool enableIdentityInsert)
    {
        if (data.Rows.Count == 0)
        {
            _logger.LogInformation("No hay datos para cargar en {TableName}", tableName);
            return 0;
        }

        _logger.LogInformation("Cargando {RowCount} filas en {TableName}...", data.Rows.Count, tableName);

        using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        try
        {
            var hasIdentity = mappings.Any(m => m.IsIdentity);

            var inserted = await dialect.BulkInsertAsync(
                connection,
                transaction,
                tableName,
                data,
                hasIdentity,
                enableIdentityInsert);

            transaction.Commit();

            _logger.LogInformation(
                "Carga completada: {RowCount} filas insertadas en {TableName}",
                inserted,
                tableName);

            return inserted;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Error al cargar datos en {TableName}", tableName);
            throw;
        }
    }
}
