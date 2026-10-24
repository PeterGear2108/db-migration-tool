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

public class TableSchema
{
    public string TableName { get; set; } = string.Empty;
    public List<ColumnSchema> Columns { get; set; } = new();
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = new();

    public List<string> PrimaryKeyColumns =>
        Columns.Where(c => c.IsPrimaryKey).Select(c => c.ColumnName).ToList();
}
