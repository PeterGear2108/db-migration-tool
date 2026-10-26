namespace DBMigrationTool.Models;

public class MigrationConfig
{
    public DatabaseEngine SourceEngine { get; set; } = DatabaseEngine.SqlServer;
    public DatabaseEngine TargetEngine { get; set; } = DatabaseEngine.SqlServer;
    public string SourceConnectionString { get; set; } = string.Empty;
    public string TargetConnectionString { get; set; } = string.Empty;
    public bool DryRun { get; set; } = false;
    public bool ConfirmBeforeTruncate { get; set; } = true;
    public bool ContinueOnError { get; set; } = true;
    public bool EnableIdentityInsert { get; set; } = true;
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Modo destructivo: en destino, elimina FKs e índices secundarios antes de truncar/cargar y los
    /// recrea al final. Resuelve que TRUNCATE falle/caiga a DELETE por FKs (lo que impide reiniciar el
    /// IDENTITY) y que índices únicos con definición desactualizada rompan la carga de datos.
    /// Solo tiene efecto si el dialecto de destino lo soporta (hoy: SQL Server). Nunca toca el origen.
    /// </summary>
    public bool DestructiveReload { get; set; } = false;

    /// <summary>
    /// Tablas excluidas por completo de cualquier selección interactiva (ej. ventas/facturación).
    /// Siempre se crean en destino sin datos.
    /// </summary>
    public List<string> AlwaysExcludedTables { get; set; } = new();

    /// <summary>
    /// Nombres de tabla seleccionadas explícitamente para copiar datos (vía appsettings o CLI).
    /// Si es null, el programa pregunta interactivamente en la terminal.
    /// </summary>
    public List<string>? SelectedTableNames { get; set; }

    /// <summary>
    /// Lista final resuelta (todas las tablas descubiertas en origen, en orden de dependencias,
    /// cada una marcada con CopyData = true/false). La llena TableDiscoveryService.
    /// </summary>
    public List<TableMigrationInfo> TablesToMigrate { get; set; } = new();

    /// <summary>
    /// Esquemas completos de origen (mismo orden que TablesToMigrate), usados para generar
    /// el DDL de las tablas que falten en destino. Los llena el flujo de descubrimiento.
    /// </summary>
    public List<TableSchema> SourceSchemas { get; set; } = new();
}
