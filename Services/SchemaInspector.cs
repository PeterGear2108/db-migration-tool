using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface ISchemaInspector
{
    Task<List<ColumnMapping>> CompareSchemasAsync(
        IDatabaseDialect sourceDialect,
        string sourceConnectionString,
        IDatabaseDialect targetDialect,
        string targetConnectionString,
        string tableName);
}

public class SchemaInspector : ISchemaInspector
{
    private readonly ILogger<SchemaInspector> _logger;

    public SchemaInspector(ILogger<SchemaInspector> logger)
    {
        _logger = logger;
    }

    public async Task<List<ColumnMapping>> CompareSchemasAsync(
        IDatabaseDialect sourceDialect,
        string sourceConnectionString,
        IDatabaseDialect targetDialect,
        string targetConnectionString,
        string tableName)
    {
        _logger.LogDebug("Analizando esquema de tabla {TableName}", tableName);

        using var sourceConnection = sourceDialect.CreateConnection(sourceConnectionString);
        await sourceConnection.OpenAsync();
        var sourceSchema = await sourceDialect.GetTableSchemaAsync(sourceConnection, tableName);

        using var targetConnection = targetDialect.CreateConnection(targetConnectionString);
        await targetConnection.OpenAsync();
        var targetSchema = await targetDialect.GetTableSchemaAsync(targetConnection, tableName);

        var sourceDict = sourceSchema.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var mappings = new List<ColumnMapping>();

        foreach (var targetCol in targetSchema.Columns)
        {
            var mapping = new ColumnMapping
            {
                ColumnName = targetCol.ColumnName,
                DataType = targetCol.DataType,
                IsNullable = targetCol.IsNullable,
                IsIdentity = targetCol.IsIdentity,
                ExistsInTarget = true
            };

            if (sourceDict.ContainsKey(targetCol.ColumnName))
            {
                mapping.ExistsInSource = true;
            }
            else
            {
                mapping.ExistsInSource = false;
                mapping.DefaultValue = DefaultValueProvider.GetDefaultValue(
                    targetCol.DataType,
                    targetCol.IsNullable,
                    targetCol.ColumnName);

                _logger.LogWarning(
                    "Columna '{Column}' no existe en origen. Se usará valor default: {Default}",
                    targetCol.ColumnName,
                    mapping.DefaultValue ?? "NULL");
            }

            mappings.Add(mapping);
        }

        _logger.LogInformation(
            "Esquema analizado: {TotalColumns} columnas, {CommonColumns} comunes",
            mappings.Count,
            mappings.Count(m => m.ExistsInSource));

        return mappings;
    }
}
