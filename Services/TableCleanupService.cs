using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface ITableCleanupService
{
    Task TruncateTablesAsync(
        IDatabaseDialect dialect,
        string connectionString,
        List<TableMigrationInfo> tables);
}

public class TableCleanupService : ITableCleanupService
{
    private readonly ILogger<TableCleanupService> _logger;

    public TableCleanupService(ILogger<TableCleanupService> logger)
    {
        _logger = logger;
    }

    public async Task TruncateTablesAsync(
        IDatabaseDialect dialect,
        string connectionString,
        List<TableMigrationInfo> tables)
    {
        _logger.LogInformation("Iniciando limpieza de {TableCount} tablas...", tables.Count);

        // Limpiar en orden inverso (por dependencias)
        var reversedTables = tables.AsEnumerable().Reverse().ToList();
        var loggerAdapter = new MicrosoftLoggerAdapter(_logger);

        using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync();

        foreach (var table in reversedTables)
        {
            try
            {
                await dialect.SetConstraintCheckingAsync(connection, table.TableName, enabled: false);
                await dialect.TruncateOrDeleteAsync(connection, table.TableName, loggerAdapter);
                await dialect.SetConstraintCheckingAsync(connection, table.TableName, enabled: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al limpiar tabla {TableName}", table.TableName);
                throw;
            }
        }

        _logger.LogInformation("Limpieza completada para todas las tablas");
    }
}
