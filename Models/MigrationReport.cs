namespace DBMigrationTool.Models;

public class MigrationReport
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public int TotalTables { get; set; }
    public int SuccessfulTables { get; set; }
    public int FailedTables { get; set; }
    public int TotalRowsMigrated { get; set; }
    public List<MigrationResult> TableResults { get; set; } = new();
    public bool DryRunMode { get; set; }
}
