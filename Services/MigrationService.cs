using System.Data;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface IMigrationService
{
    Task<MigrationReport> ExecuteMigrationAsync(MigrationConfig config);
}

public class MigrationService : IMigrationService
{
    private readonly ISchemaInspector _schemaInspector;
    private readonly IDataExtractor _dataExtractor;
    private readonly IDataLoader _dataLoader;
    private readonly ITableCleanupService _cleanupService;
    private readonly ISchemaSyncService _schemaSyncService;
    private readonly IReportingService _reportingService;
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(
        ISchemaInspector schemaInspector,
        IDataExtractor dataExtractor,
        IDataLoader dataLoader,
        ITableCleanupService cleanupService,
        ISchemaSyncService schemaSyncService,
        IReportingService reportingService,
        ILogger<MigrationService> logger)
    {
        _schemaInspector = schemaInspector;
        _dataExtractor = dataExtractor;
        _dataLoader = dataLoader;
        _cleanupService = cleanupService;
        _schemaSyncService = schemaSyncService;
        _reportingService = reportingService;
        _logger = logger;
    }

    public async Task<MigrationReport> ExecuteMigrationAsync(MigrationConfig config)
    {
        var sourceDialect = DatabaseDialectFactory.Create(config.SourceEngine);
        var targetDialect = DatabaseDialectFactory.Create(config.TargetEngine);

        var tablesToCopy = config.TablesToMigrate.Where(t => t.CopyData).ToList();
        var tablesSchemaOnly = config.TablesToMigrate.Where(t => !t.CopyData).ToList();

        var report = new MigrationReport
        {
            StartTime = DateTime.Now,
            DryRunMode = config.DryRun,
            TotalTables = tablesToCopy.Count
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("=== INICIANDO MIGRACIÓN DE DATOS ===");
            _logger.LogInformation("Origen: {SourceEngine}  →  Destino: {TargetEngine}", config.SourceEngine, config.TargetEngine);
            _logger.LogInformation("Validando conexiones...");

            await ValidateConnectionAsync(sourceDialect, config.SourceConnectionString, "ORIGEN");
            await ValidateConnectionAsync(targetDialect, config.TargetConnectionString, "DESTINO");

            // 1. Asegurar que todas las tablas (seleccionadas o no) existan en destino
            _logger.LogInformation("");
            _logger.LogInformation("=== SINCRONIZANDO ESQUEMA EN DESTINO ===");
            await _schemaSyncService.EnsureTablesExistAsync(
                targetDialect, config.TargetConnectionString, config.SourceSchemas);

            // 2. Modo destructivo (solo destino, solo si lo soporta el dialecto): eliminar FKs e índices
            //    secundarios ANTES de truncar, para que TRUNCATE no caiga a DELETE (lo que impide reiniciar
            //    el IDENTITY) y para que la carga no choque con índices únicos desactualizados.
            var destructiveMode = config.DestructiveReload && targetDialect.SupportsDestructiveReload;

            if (!config.DryRun && destructiveMode)
            {
                _logger.LogInformation("");
                _logger.LogInformation("=== MODO DESTRUCTIVO: eliminando FKs e índices secundarios en destino ===");

                using var ddlConnection = targetDialect.CreateConnection(config.TargetConnectionString);
                await ddlConnection.OpenAsync();

                await targetDialect.DropForeignKeysAsync(ddlConnection);
                foreach (var schema in config.SourceSchemas)
                {
                    await targetDialect.DropSecondaryIndexesAsync(ddlConnection, schema.TableName);
                }
            }
            else if (config.DestructiveReload && !destructiveMode)
            {
                _logger.LogWarning(
                    "DestructiveReload está activo pero {Engine} no lo soporta; se ignora (truncate normal)",
                    config.TargetEngine);
            }

            try
            {
                // 3. Fase de limpieza: TODAS las tablas quedan vacías primero (orden inverso de dependencias)
                if (!config.DryRun)
                {
                    _logger.LogInformation("");
                    _logger.LogInformation("=== FASE DE LIMPIEZA ===");
                    await _cleanupService.TruncateTablesAsync(
                        targetDialect, config.TargetConnectionString, config.TablesToMigrate);
                }
                else
                {
                    _logger.LogInformation("MODO DRY-RUN: Se omite la limpieza de tablas");
                }

                // 4. Fase de migración: solo tablas seleccionadas reciben datos
                _logger.LogInformation("");
                _logger.LogInformation("=== FASE DE MIGRACIÓN ===");
                _logger.LogInformation("Tablas con datos: {Copy} | Tablas solo-esquema (sin datos): {Skip}",
                    tablesToCopy.Count, tablesSchemaOnly.Count);

                foreach (var table in tablesSchemaOnly)
                {
                    _logger.LogInformation("⊘ {TableName}: no seleccionada, se deja sin datos", table.TableName);
                }

                foreach (var table in tablesToCopy)
                {
                    var tableResult = await MigrateTableAsync(table, sourceDialect, targetDialect, config);
                    report.TableResults.Add(tableResult);

                    if (tableResult.Success)
                    {
                        report.SuccessfulTables++;
                        report.TotalRowsMigrated += tableResult.RowsInserted;
                    }
                    else
                    {
                        report.FailedTables++;

                        if (!config.ContinueOnError)
                        {
                            _logger.LogError("Abortando migración debido a error en {TableName}", table.TableName);
                            break;
                        }
                    }
                }
            }
            finally
            {
                // 5. Recrear índices y FKs eliminados en el paso 2, siempre (incluso si algo falló arriba),
                //    para no dejar destino sin constraints por un error a mitad de camino.
                if (!config.DryRun && destructiveMode)
                {
                    _logger.LogInformation("");
                    _logger.LogInformation("=== MODO DESTRUCTIVO: recreando índices y FKs en destino ===");

                    using var ddlConnection = targetDialect.CreateConnection(config.TargetConnectionString);
                    await ddlConnection.OpenAsync();

                    foreach (var schema in config.SourceSchemas)
                    {
                        foreach (var indexDdl in targetDialect.GenerateCreateIndexDdl(schema))
                        {
                            using var indexCommand = ddlConnection.CreateCommand();
                            indexCommand.CommandText = indexDdl;

                            try
                            {
                                await indexCommand.ExecuteNonQueryAsync();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "No se pudo recrear el índice con DDL: {Ddl}", indexDdl);
                            }
                        }
                    }

                    await targetDialect.RecreateForeignKeysAsync(ddlConnection);
                }
            }

            stopwatch.Stop();
            report.EndTime = DateTime.Now;
            report.TotalDuration = stopwatch.Elapsed;

            var finalReport = _reportingService.GenerateFinalReport(report);
            Console.WriteLine(finalReport);
            _logger.LogInformation(finalReport);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fatal durante la migración");
            stopwatch.Stop();
            report.EndTime = DateTime.Now;
            report.TotalDuration = stopwatch.Elapsed;
            throw;
        }
    }

    private async Task<MigrationResult> MigrateTableAsync(
        TableMigrationInfo tableInfo,
        IDatabaseDialect sourceDialect,
        IDatabaseDialect targetDialect,
        MigrationConfig config)
    {
        var result = new MigrationResult
        {
            TableName = tableInfo.TableName
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _reportingService.LogTableStart(tableInfo.TableName);

            var mappings = await _schemaInspector.CompareSchemasAsync(
                sourceDialect,
                config.SourceConnectionString,
                targetDialect,
                config.TargetConnectionString,
                tableInfo.TableName);

            result.ColumnMappings = mappings;

            var missingColumns = mappings.Where(m => !m.ExistsInSource).ToList();
            foreach (var missing in missingColumns)
            {
                var warning = $"{tableInfo.TableName}.{missing.ColumnName}: No existe en origen → default: {missing.DefaultValue}";
                result.Warnings.Add(warning);
            }

            var columnsToExtract = mappings
                .Where(m => m.ExistsInSource)
                .Select(m => m.ColumnName)
                .ToList();

            var sourceData = await _dataExtractor.ExtractTableDataAsync(
                sourceDialect,
                config.SourceConnectionString,
                tableInfo.TableName,
                columnsToExtract);

            result.RowsExtracted = sourceData.Rows.Count;

            if (sourceData.Rows.Count == 0)
            {
                _logger.LogInformation("No hay datos para migrar en {TableName}", tableInfo.TableName);
                result.Success = true;
                result.Duration = stopwatch.Elapsed;
                _reportingService.LogTableComplete(result);
                return result;
            }

            var transformedData = TransformData(sourceData, mappings);

            if (!config.DryRun)
            {
                result.RowsInserted = await _dataLoader.LoadDataAsync(
                    targetDialect,
                    config.TargetConnectionString,
                    tableInfo.TableName,
                    transformedData,
                    mappings,
                    config.EnableIdentityInsert && tableInfo.HasIdentityColumn);

                using var countConnection = targetDialect.CreateConnection(config.TargetConnectionString);
                await countConnection.OpenAsync();
                var actualCount = await targetDialect.CountRowsAsync(countConnection, tableInfo.TableName);
                if (actualCount != result.RowsInserted)
                {
                    result.Warnings.Add($"Discrepancia: esperadas {result.RowsInserted}, encontradas {actualCount}");
                }
            }
            else
            {
                _logger.LogInformation("MODO DRY-RUN: Se omite la carga de {RowCount} filas", sourceData.Rows.Count);
                result.RowsInserted = sourceData.Rows.Count;
            }

            result.Success = true;
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            _reportingService.LogTableComplete(result);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.Success = false;
            result.Duration = stopwatch.Elapsed;
            result.Errors.Add(ex.Message);

            _logger.LogError(ex, "Error al migrar tabla {TableName}", tableInfo.TableName);
            _reportingService.LogTableComplete(result);

            return result;
        }
    }

    private DataTable TransformData(DataTable sourceData, List<ColumnMapping> mappings)
    {
        var transformed = new DataTable(sourceData.TableName);

        foreach (var mapping in mappings)
        {
            var dataType = mapping.IsNullable ? typeof(object) : GetClrType(mapping.DataType);
            transformed.Columns.Add(mapping.ColumnName, dataType);
        }

        foreach (DataRow sourceRow in sourceData.Rows)
        {
            var targetRow = transformed.NewRow();

            foreach (var mapping in mappings)
            {
                if (mapping.ExistsInSource)
                {
                    targetRow[mapping.ColumnName] = sourceRow[mapping.ColumnName];
                }
                else
                {
                    targetRow[mapping.ColumnName] = mapping.DefaultValue ?? DBNull.Value;
                }
            }

            transformed.Rows.Add(targetRow);
        }

        return transformed;
    }

    private Type GetClrType(string sqlDataType)
    {
        return sqlDataType.ToLower() switch
        {
            "int" or "bigint" or "smallint" or "tinyint" or "integer" or "int4" or "int8" or "int2" => typeof(int),
            "decimal" or "numeric" or "money" => typeof(decimal),
            "bit" or "boolean" or "bool" => typeof(bool),
            "varchar" or "nvarchar" or "char" or "nchar" or "text" or "character varying" => typeof(string),
            "datetime" or "datetime2" or "smalldatetime" or "timestamp" or "timestamp without time zone" => typeof(DateTime),
            "datetimeoffset" or "timestamptz" or "timestamp with time zone" => typeof(DateTimeOffset),
            "uniqueidentifier" or "uuid" => typeof(Guid),
            "binary" or "varbinary" or "image" or "bytea" => typeof(byte[]),
            _ => typeof(object)
        };
    }

    private async Task ValidateConnectionAsync(IDatabaseDialect dialect, string connectionString, string name)
    {
        using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync();
        _logger.LogInformation("✓ Conexión a {Name} exitosa: {Database}", name, connection.Database);
    }

}
