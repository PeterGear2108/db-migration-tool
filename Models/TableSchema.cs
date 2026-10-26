namespace DBMigrationTool.Models;

public class ColumnSchema
{
    public string ColumnName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int OrdinalPosition { get; set; }
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsPrimaryKey { get; set; }
    public long? MaxLength { get; set; }
    public int? NumericPrecision { get; set; }
    public int? NumericScale { get; set; }
}

public class ForeignKeyInfo
{
    public string ConstraintName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string ReferencedTableName { get; set; } = string.Empty;
    public string ReferencedColumnName { get; set; } = string.Empty;
}

public class IndexInfo
{
    public string IndexName { get; set; } = string.Empty;
    public bool IsUnique { get; set; }
    public List<string> Columns { get; set; } = new();

    /// <summary>Predicado del índice filtrado (ej. "([Codigo] IS NOT NULL)"), si aplica. Solo SQL Server/Postgres.</summary>
    public string? FilterDefinition { get; set; }
}

/// <summary>Definición completa de una FK, capturada desde destino antes de eliminarla, para poder recrearla idéntica después.</summary>
public class ForeignKeyDefinition
{
    public string ConstraintName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public string ReferencedTableName { get; set; } = string.Empty;
    public List<string> ReferencedColumns { get; set; } = new();
    public string DeleteAction { get; set; } = "NO ACTION";
    public string UpdateAction { get; set; } = "NO ACTION";
}

public class TableSchema
{
    public string TableName { get; set; } = string.Empty;
    public List<ColumnSchema> Columns { get; set; } = new();
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = new();
    public List<IndexInfo> Indexes { get; set; } = new();

    public List<string> PrimaryKeyColumns =>
        Columns.Where(c => c.IsPrimaryKey).Select(c => c.ColumnName).ToList();
}
