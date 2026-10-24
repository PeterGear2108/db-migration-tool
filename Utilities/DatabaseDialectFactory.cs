using DBMigrationTool.Models;

namespace DBMigrationTool.Utilities;

public static class DatabaseDialectFactory
{
    public static IDatabaseDialect Create(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => new SqlServerDialect(),
        DatabaseEngine.PostgreSql => new PostgreSqlDialect(),
        DatabaseEngine.MySql => new MySqlDialect(),
        _ => throw new NotSupportedException($"Motor de base de datos no soportado: {engine}")
    };
}
