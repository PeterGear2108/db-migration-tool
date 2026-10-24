using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface ITableDiscoveryService
{
    /// <summary>
    /// Descubre todas las tablas del origen, construye el grafo de dependencias (FKs)
    /// y devuelve la lista ordenada topológicamente con sus esquemas completos.
    /// </summary>
    Task<List<TableSchema>> DiscoverAsync(IDatabaseDialect sourceDialect, string sourceConnectionString);

    List<TableMigrationInfo> BuildMigrationPlan(
        List<TableSchema> orderedSchemas,
        List<string> selectedTableNames,
        List<string> alwaysExcludedTables);
}

public class TableDiscoveryService : ITableDiscoveryService
{
    private readonly ILogger<TableDiscoveryService> _logger;

    public TableDiscoveryService(ILogger<TableDiscoveryService> logger)
    {
        _logger = logger;
    }

    public async Task<List<TableSchema>> DiscoverAsync(IDatabaseDialect sourceDialect, string sourceConnectionString)
    {
        using var connection = sourceDialect.CreateConnection(sourceConnectionString);
        await connection.OpenAsync();

        var tableNames = await sourceDialect.GetTableNamesAsync(connection);
        _logger.LogInformation("Descubiertas {Count} tablas en origen", tableNames.Count);

        var schemas = new List<TableSchema>();
        foreach (var tableName in tableNames)
        {
            schemas.Add(await sourceDialect.GetTableSchemaAsync(connection, tableName));
        }

        return TopologicalSort(schemas);
    }

    private List<TableSchema> TopologicalSort(List<TableSchema> schemas)
    {
        var byName = schemas.ToDictionary(s => s.TableName, StringComparer.OrdinalIgnoreCase);
        var sorted = new List<TableSchema>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var schema in schemas)
        {
            Visit(schema, byName, visited, visiting, sorted);
        }

        return sorted;
    }

    private void Visit(
        TableSchema table,
        Dictionary<string, TableSchema> byName,
        HashSet<string> visited,
        HashSet<string> visiting,
        List<TableSchema> sorted)
    {
        if (visited.Contains(table.TableName)) return;

        if (visiting.Contains(table.TableName))
        {
            _logger.LogWarning("Dependencia circular detectada en {TableName}, se omite el ciclo", table.TableName);
            return;
        }

        visiting.Add(table.TableName);

        foreach (var fk in table.ForeignKeys)
        {
            if (string.Equals(fk.ReferencedTableName, table.TableName, StringComparison.OrdinalIgnoreCase))
                continue; // auto-referencia

            if (byName.TryGetValue(fk.ReferencedTableName, out var referenced))
            {
                Visit(referenced, byName, visited, visiting, sorted);
            }
        }

        visiting.Remove(table.TableName);
        visited.Add(table.TableName);
        sorted.Add(table);
    }

    public List<TableMigrationInfo> BuildMigrationPlan(
        List<TableSchema> orderedSchemas,
        List<string> selectedTableNames,
        List<string> alwaysExcludedTables)
    {
        var selectedSet = new HashSet<string>(selectedTableNames, StringComparer.OrdinalIgnoreCase);
        var excludedSet = new HashSet<string>(alwaysExcludedTables, StringComparer.OrdinalIgnoreCase);

        var plan = new List<TableMigrationInfo>();
        for (var i = 0; i < orderedSchemas.Count; i++)
        {
            var schema = orderedSchemas[i];
            var pk = schema.PrimaryKeyColumns;
            var identityCol = schema.Columns.FirstOrDefault(c => c.IsIdentity);

            plan.Add(new TableMigrationInfo
            {
                TableName = schema.TableName,
                Order = i,
                DependsOn = schema.ForeignKeys
                    .Select(fk => fk.ReferencedTableName)
                    .Where(t => !string.Equals(t, schema.TableName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                HasIdentityColumn = identityCol != null,
                IdentityColumnName = identityCol?.ColumnName ?? "Id",
                CopyData = !excludedSet.Contains(schema.TableName) && selectedSet.Contains(schema.TableName)
            });
        }

        return plan;
    }
}
