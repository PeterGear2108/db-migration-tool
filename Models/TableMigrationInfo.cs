namespace DBMigrationTool.Models;

public class TableMigrationInfo
{
    public string TableName { get; set; } = string.Empty;
    public int Order { get; set; }
    public List<string> DependsOn { get; set; } = new();
    public bool HasIdentityColumn { get; set; } = true;
    public string IdentityColumnName { get; set; } = "Id";
    public bool IsJunctionTable { get; set; } = false;

    /// <summary>
    /// true: se copian los datos de origen. false: la tabla se crea/queda con el esquema
    /// pero sin filas (ver MigrationConfig.TablesToMigrate vs. TablesToSkip).
    /// </summary>
    public bool CopyData { get; set; } = true;
}
