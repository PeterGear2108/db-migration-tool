namespace DBMigrationTool.Utilities;

public static class DefaultValueProvider
{
    public static object GetDefaultValue(string sqlDataType, bool isNullable, string? columnName = null)
    {
        if (isNullable)
            return DBNull.Value;

        // Valores especiales para campos de auditoría
        if (columnName != null)
        {
            return columnName.ToLower() switch
            {
                "createdby" or "modifiedby" => "migration-tool",
                "createdat" or "modifiedat" => DateTimeOffset.UtcNow,
                _ => GetDefaultByType(sqlDataType)
            };
        }

        return GetDefaultByType(sqlDataType);
    }

    private static object GetDefaultByType(string sqlDataType)
    {
        return sqlDataType.ToLower() switch
        {
            "int" or "bigint" or "smallint" or "tinyint" => 0,
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => 0m,
            "bit" => false,
            "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" => string.Empty,
            "datetime" or "datetime2" or "smalldatetime" => DateTime.UtcNow,
            "datetimeoffset" => DateTimeOffset.UtcNow,
            "date" => DateOnly.FromDateTime(DateTime.UtcNow),
            "time" => TimeOnly.FromDateTime(DateTime.UtcNow),
            "uniqueidentifier" => Guid.NewGuid(),
            "binary" or "varbinary" or "image" => Array.Empty<byte>(),
            _ => DBNull.Value
        };
    }
}
