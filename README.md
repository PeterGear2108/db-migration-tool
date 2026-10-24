# db-migration-tool

Herramienta de migración de datos **producción → test**, genérica para **SQL Server, PostgreSQL y MySQL** (en cualquier combinación origen/destino).

## Qué hace

1. Se conecta a origen y destino (pueden ser motores distintos).
2. Descubre automáticamente todas las tablas de origen y su orden de dependencias (vía foreign keys) — no hace falta mantener una lista manual.
3. Te deja elegir, con un checklist interactivo en la terminal, qué tablas copian datos. El resto se crea (si falta) o se deja **vacía**, solo con su esquema.
4. Sincroniza en destino el esquema de cualquier tabla que no exista (traduce tipos de datos entre motores).
5. Limpia destino y copia los datos de las tablas seleccionadas, respetando el orden de dependencias.

## Configuración (`appsettings.json`)

```jsonc
{
  "SourceEngine": "SqlServer",   // SqlServer | PostgreSql | MySql
  "TargetEngine": "SqlServer",
  "ConnectionStrings": {
    "Production": "...",
    "Test": "..."
  },
  "MigrationSettings": {
    "DryRun": true,
    "ConfirmBeforeTruncate": true,
    "ContinueOnError": false,
    "EnableIdentityInsert": true,
    "BatchSize": 1000
  },
  "AlwaysExcludedTables": ["Sales", "SalesOrderDetail"], // nunca se ofrecen para migrar datos (ej. ventas)
  "SelectedTableNames": null // null = preguntar interactivamente; o una lista fija para correr sin interacción (CI)
}
```

- Si `SelectedTableNames` es `null`, el programa muestra un checklist interactivo (flechas + espacio) con todas las tablas descubiertas, excepto las de `AlwaysExcludedTables`.
- Si `SelectedTableNames` trae una lista, se usa directamente (útil para correr el tool sin intervención humana, p. ej. en un pipeline).

## Limitaciones conocidas

- El DDL autogenerado para tablas faltantes en destino traduce columnas, tipos, nulabilidad y primary key, pero **no** recrea foreign keys, índices ni checks — solo asegura que la tabla exista para poder insertar datos u operar contra ella en test.
- La detección de dependencias asume que las FKs están declaradas en la base de datos de origen.

## Ejecutar

```bash
dotnet run
```
