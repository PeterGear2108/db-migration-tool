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
    "DestructiveReload": false,
    "BatchSize": 1000
  },
  "AlwaysExcludedTables": ["Sales", "SalesOrderDetail"], // nunca se ofrecen para migrar datos (ej. ventas)
  "SelectedTableNames": null // null = preguntar interactivamente; o una lista fija para correr sin interacción (CI)
}
```

- Si `SelectedTableNames` es `null`, el programa muestra un checklist interactivo (flechas + espacio) con todas las tablas descubiertas, excepto las de `AlwaysExcludedTables`.
- Si `SelectedTableNames` trae una lista, se usa directamente (útil para correr el tool sin intervención humana, p. ej. en un pipeline).

## Modo destructivo (`DestructiveReload`)

Solo aplica a **destino**, nunca a origen. Pensado para cuando el truncate normal no alcanza: en SQL Server, `TRUNCATE` falla si la tabla es referenciada por una FK (incluso con `NOCHECK`) y el tool cae a `DELETE`, que **no reinicia el IDENTITY**; además, si un índice único en destino quedó con una definición desactualizada (p. ej. le falta un filtro `WHERE ... IS NOT NULL`), la carga de datos puede fallar por violación de unicidad.

Con `DestructiveReload: true` (solo tiene efecto en SQL Server; en PostgreSQL/MySQL se ignora con un warning, porque ya truncan y reinician el autoincremento sin este rodeo), el flujo en destino pasa a ser:

1. Elimina **todas** las FKs de destino (capturando su definición completa: columnas, tabla referenciada, `ON DELETE`/`ON UPDATE`).
2. Elimina los índices secundarios (no PK) de cada tabla, incluyendo los que están implementados como `UNIQUE CONSTRAINT`.
3. `TRUNCATE` real (ya sin bloqueos de FK) → esta vez sí reinicia el IDENTITY.
4. Carga los datos de origen, con `IDENTITY_INSERT` por tabla.
5. Recrea los índices (con su filtro, si lo tenían) y las FKs capturadas en el paso 1 — siempre, incluso si algo falló en el camino, para no dejar destino sin constraints.

## Limitaciones conocidas

- El DDL autogenerado para tablas que **no existen** en destino traduce columnas, tipos, nulabilidad, primary key, IDENTITY/AUTO_INCREMENT/SERIAL e índices secundarios (incluyendo el filtro en SQL Server), pero no recrea FKs — solo asegura que la tabla exista para poder insertar datos u operar contra ella en test. Si la tabla ya existe, no se toca su esquema en absoluto.
- La detección de dependencias asume que las FKs están declaradas en la base de datos de origen.

## Ejecutar

```bash
dotnet run
```
