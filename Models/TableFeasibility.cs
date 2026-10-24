namespace DBMigrationTool.Models;

public enum FeasibilityLevel
{
    Ok,
    Warning,
    Risk
}

public class TableFeasibility
{
    public string TableName { get; set; } = string.Empty;
    public bool CopyData { get; set; }
    public bool ExistsInTarget { get; set; }
    public long SourceRowCount { get; set; }
    public long TargetRowCount { get; set; }
    public int MissingColumnsInSource { get; set; }
    public List<string> Notes { get; set; } = new();
    public FeasibilityLevel Level { get; set; } = FeasibilityLevel.Ok;
}
