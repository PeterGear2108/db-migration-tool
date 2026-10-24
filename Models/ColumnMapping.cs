namespace DBMigrationTool.Models;

public class ColumnMapping
{
    public string ColumnName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool ExistsInSource { get; set; }
    public bool ExistsInTarget { get; set; }
    public bool IsIdentity { get; set; }
    public object? DefaultValue { get; set; }
}
