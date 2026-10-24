using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface IMigrationFeasibilityService
{
    /// <summary>
    /// Analiza, antes de ejecutar nada, qué va a pasar con cada tabla del plan:
    /// cuántas filas hay en origen/destino, si la tabla existe en destino, columnas
    /// que no calzan y cualquier otra señal de riesgo (datos que se van a perder, etc.).
    /// </summary>
    Task<List<TableFeasibility>> AnalyzeAsync(
        IDatabaseDialect sourceDialect,
        string sourceConnectionString,
        IDatabaseDialect targetDialect,
        string targetConnectionString,
        List<TableMigrationInfo> plan,
        List<TableSchema> sourceSchemas);
}

public class MigrationFeasibilityService : IMigrationFeasibilityService
{
    private readonly ILogger<MigrationFeasibilityService> _logger;

    public MigrationFeasibilityService(ILogger<MigrationFeasibilityService> logger)
    {
        _logger = logger;
    }

    public async Task<List<TableFeasibility>> AnalyzeAsync(
        IDatabaseDialect sourceDialect,
        string sourceConnectionString,
        IDatabaseDialect targetDialect,
        string targetConnectionString,
        List<TableMigrationInfo> plan,
        List<TableSchema> sourceSchemas)
    {
        var sourceSchemaByName = sourceSchemas.ToDictionary(s => s.TableName, StringComparer.OrdinalIgnoreCase);
        var results = new List<TableFeasibility>();

        using var sourceConnection = sourceDialect.CreateConnection(sourceConnectionString);
        await sourceConnection.OpenAsync();

        using var targetConnection = targetDialect.CreateConnection(targetConnectionString);
        await targetConnection.OpenAsync();

        foreach (var table in plan)
        {
            var feasibility = new TableFeasibility
            {
                TableName = table.TableName,
                CopyData = table.CopyData
            };

            try
            {
                feasibility.ExistsInTarget = await targetDialect.TableExistsAsync(targetConnection, table.TableName);

                if (!feasibility.ExistsInTarget)
                {
                    feasibility.Notes.Add("No existe en destino: se creará (DDL generado) sin FKs ni índices");
                    feasibility.Level = Max(feasibility.Level, FeasibilityLevel.Warning);
                }

                if (sourceSchemaByName.TryGetValue(table.TableName, out var sourceSchema))
                {
                    feasibility.SourceRowCount = await sourceDialect.CountRowsAsync(sourceConnection, table.TableName);

                    if (feasibility.ExistsInTarget)
                    {
                        feasibility.TargetRowCount = await targetDialect.CountRowsAsync(targetConnection, table.TableName);

                        var targetSchema = await targetDialect.GetTableSchemaAsync(targetConnection, table.TableName);
                        var sourceColumnNames = new HashSet<string>(
                            sourceSchema.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);

                        feasibility.MissingColumnsInSource = targetSchema.Columns
                            .Count(c => !sourceColumnNames.Contains(c.ColumnName));

                        if (feasibility.MissingColumnsInSource > 0)
                        {
                            feasibility.Notes.Add(
                                $"{feasibility.MissingColumnsInSource} columna(s) de destino no existen en origen → se rellenan con valores default");
                            feasibility.Level = Max(feasibility.Level, FeasibilityLevel.Warning);
                        }

                        if (feasibility.TargetRowCount > 0)
                        {
                            feasibility.Notes.Add(table.CopyData
                                ? $"Se eliminarán {feasibility.TargetRowCount:N0} filas existentes en destino antes de copiar"
                                : $"Se eliminarán {feasibility.TargetRowCount:N0} filas existentes en destino (tabla no seleccionada, quedará vacía)");
                            feasibility.Level = Max(feasibility.Level, FeasibilityLevel.Warning);
                        }
                    }

                    if (table.CopyData && feasibility.SourceRowCount > 500_000)
                    {
                        feasibility.Notes.Add($"Tabla grande ({feasibility.SourceRowCount:N0} filas): la copia puede tardar");
                        feasibility.Level = Max(feasibility.Level, FeasibilityLevel.Warning);
                    }
                }
                else
                {
                    feasibility.Notes.Add("No se encontró el esquema de origen (inconsistencia interna)");
                    feasibility.Level = FeasibilityLevel.Risk;
                }
            }
            catch (Exception ex)
            {
                feasibility.Notes.Add($"No se pudo analizar: {ex.Message}");
                feasibility.Level = FeasibilityLevel.Risk;
                _logger.LogWarning(ex, "Fallo al analizar viabilidad de {TableName}", table.TableName);
            }

            results.Add(feasibility);
        }

        return results;
    }

    private static FeasibilityLevel Max(FeasibilityLevel a, FeasibilityLevel b) => (FeasibilityLevel)Math.Max((int)a, (int)b);
}
