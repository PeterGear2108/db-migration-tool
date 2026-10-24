namespace DBMigrationTool.Models;

public class MigrationResult
{
    public string TableName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int RowsExtracted { get; set; }
    public int RowsInserted { get; set; }
    public int RowsFailed { get; set; }
    public TimeSpan Duration { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<ColumnMapping> ColumnMappings { get; set; } = new();
}
