using System.Data;
using Microsoft.Extensions.Logging;
using DBMigrationTool.Utilities;

namespace DBMigrationTool.Services;

public interface IDataExtractor
{
    Task<DataTable> ExtractTableDataAsync(
        IDatabaseDialect dialect,
        string connectionString,
        string tableName,
        List<string> columns);
}

public class DataExtractor : IDataExtractor
{
    private readonly ILogger<DataExtractor> _logger;

    public DataExtractor(ILogger<DataExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<DataTable> ExtractTableDataAsync(
        IDatabaseDialect dialect,
        string connectionString,
        string tableName,
        List<string> columns)
    {
        _logger.LogInformation("Extrayendo datos de {TableName}...", tableName);

        var dataTable = new DataTable(tableName);
        var query = dialect.GenerateSelectStatement(tableName, columns);

        using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300; // 5 minutes

        using var reader = await command.ExecuteReaderAsync();
        dataTable.Load(reader);

        _logger.LogInformation(
            "Extraídas {RowCount} filas de {TableName}",
            dataTable.Rows.Count,
            tableName);

        return dataTable;
    }
}
