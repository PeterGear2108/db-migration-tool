using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface ISchemaSyncService
{
    /// <summary>
    /// Para cada tabla del origen, crea la tabla en destino (con DDL traducido al motor
    /// de destino) si todavía no existe. No toca tablas que ya existen.
    /// </summary>
    Task EnsureTablesExistAsync(
        IDatabaseDialect targetDialect,
        string targetConnectionString,
        List<TableSchema> sourceSchemasInOrder);
}

public class SchemaSyncService : ISchemaSyncService
{
    private readonly ILogger<SchemaSyncService> _logger;

    public SchemaSyncService(ILogger<SchemaSyncService> logger)
    {
        _logger = logger;
    }

    public async Task EnsureTablesExistAsync(
        IDatabaseDialect targetDialect,
        string targetConnectionString,
        List<TableSchema> sourceSchemasInOrder)
    {
        using var connection = targetDialect.CreateConnection(targetConnectionString);
        await connection.OpenAsync();

        foreach (var schema in sourceSchemasInOrder)
        {
            var exists = await targetDialect.TableExistsAsync(connection, schema.TableName);
            if (exists)
            {
                continue;
            }

            var ddl = targetDialect.GenerateCreateTableDdl(schema);
            _logger.LogInformation("Tabla {TableName} no existe en destino. Creando esquema...", schema.TableName);
            _logger.LogDebug("{Ddl}", ddl);

            using var command = connection.CreateCommand();
            command.CommandText = ddl;

            try
            {
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("✓ Tabla {TableName} creada en destino (sin datos)", schema.TableName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "No se pudo crear automáticamente la tabla {TableName} en destino. " +
                    "Revise el DDL generado y créela manualmente si es necesario.",
                    schema.TableName);
            }
        }
    }
}
