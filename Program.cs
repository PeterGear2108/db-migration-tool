using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;
using DBMigrationTool.Services;
using DBMigrationTool.Utilities;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;

AnsiConsole.Write(new Rule("[bold cyan]DB MIGRATION TOOL[/]").RuleStyle("grey"));
AnsiConsole.MarkupLine("Motor multi-base de datos: [green]SQL Server[/] · [green]PostgreSQL[/] · [green]MySQL[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[grey]Cómo funciona:[/]");
AnsiConsole.MarkupLine("  1. Se conecta a [bold]origen[/] y [bold]destino[/] (pueden ser motores distintos).");
AnsiConsole.MarkupLine("  2. Descubre TODAS las tablas de origen y su orden de dependencias (FKs).");
AnsiConsole.MarkupLine("  3. Tú eliges qué tablas [green]copian datos[/]; el resto se crea/deja [yellow]vacía[/] (solo esquema).");
AnsiConsole.MarkupLine("  4. Analiza viabilidad (filas, columnas, riesgos) antes de tocar nada y pide confirmación.");
AnsiConsole.MarkupLine("  5. Limpia destino, sincroniza esquemas faltantes y copia los datos seleccionados.");
AnsiConsole.WriteLine();

try
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();

    var services = new ServiceCollection();

    services.AddLogging(builder =>
    {
        builder.AddConsole();
        builder.AddConfiguration(configuration.GetSection("Logging"));
    });

    services.AddSingleton<IConfiguration>(configuration);
    services.AddTransient<IMigrationService, MigrationService>();
    services.AddTransient<ISchemaInspector, SchemaInspector>();
    services.AddTransient<IDataExtractor, DataExtractor>();
    services.AddTransient<IDataLoader, DataLoader>();
    services.AddTransient<ITableCleanupService, TableCleanupService>();
    services.AddTransient<ISchemaSyncService, SchemaSyncService>();
    services.AddTransient<ITableDiscoveryService, TableDiscoveryService>();
    services.AddTransient<ITableSelector, InteractiveTableSelector>();
    services.AddTransient<IReportingService, ReportingService>();
    services.AddTransient<IConnectionSetupService, ConnectionSetupService>();
    services.AddTransient<IMigrationFeasibilityService, MigrationFeasibilityService>();

    var serviceProvider = services.BuildServiceProvider();

    var config = new MigrationConfig
    {
        DryRun = configuration.GetValue<bool>("MigrationSettings:DryRun"),
        ConfirmBeforeTruncate = configuration.GetValue<bool>("MigrationSettings:ConfirmBeforeTruncate"),
        ContinueOnError = configuration.GetValue<bool>("MigrationSettings:ContinueOnError"),
        EnableIdentityInsert = configuration.GetValue<bool>("MigrationSettings:EnableIdentityInsert"),
        DestructiveReload = configuration.GetValue<bool>("MigrationSettings:DestructiveReload"),
        BatchSize = configuration.GetValue<int>("MigrationSettings:BatchSize"),
        AlwaysExcludedTables = configuration.GetSection("AlwaysExcludedTables").Get<List<string>>() ?? new(),
        SelectedTableNames = configuration.GetSection("SelectedTableNames").Get<List<string>>()
    };

    // 0a. Selector de modo: siempre se pregunta y sobreescribe lo que diga appsettings.json
    var defaultModeIndex = config.DryRun ? 0 : 1;
    var modeChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title($"Selecciona el modo de ejecución [grey](appsettings.json sugiere: {(config.DryRun ? "Simular" : "Ejecutar")})[/]:")
            .AddChoices("Simular (dry-run) — no modifica nada", "Ejecutar migración real"));

    config.DryRun = modeChoice.StartsWith("Simular");

    if (config.DryRun)
    {
        AnsiConsole.MarkupLine("[cyan]ℹ️  MODO DRY-RUN ACTIVADO — no se realizarán cambios en la base de datos[/]");
    }
    else
    {
        AnsiConsole.MarkupLine("[bold red]⚠ MODO EJECUCIÓN REAL — se modificará la base de datos destino[/]");
    }
    AnsiConsole.WriteLine();

    // 0b. Configurar/validar conexiones de forma interactiva (motor + cadena), con reintento si fallan
    var connectionSetup = serviceProvider.GetRequiredService<IConnectionSetupService>();

    var sourceConn = await connectionSetup.ResolveConnectionAsync(
        "ORIGEN",
        configuration.GetValue<DatabaseEngine?>("SourceEngine") ?? DatabaseEngine.SqlServer,
        configuration.GetConnectionString("Production"));

    var targetConn = await connectionSetup.ResolveConnectionAsync(
        "DESTINO",
        configuration.GetValue<DatabaseEngine?>("TargetEngine") ?? DatabaseEngine.SqlServer,
        configuration.GetConnectionString("Test"));

    config.SourceEngine = sourceConn.Engine;
    config.SourceConnectionString = sourceConn.ConnectionString;
    config.TargetEngine = targetConn.Engine;
    config.TargetConnectionString = targetConn.ConnectionString;

    // 1. Descubrir el esquema completo de origen (tablas + FKs, ya ordenado por dependencias)
    var discoveryService = serviceProvider.GetRequiredService<ITableDiscoveryService>();
    var sourceDialect = DatabaseDialectFactory.Create(config.SourceEngine);
    var targetDialect = DatabaseDialectFactory.Create(config.TargetEngine);

    await AnsiConsole.Status().StartAsync("Descubriendo tablas en origen...", async _ =>
    {
        config.SourceSchemas = await discoveryService.DiscoverAsync(sourceDialect, config.SourceConnectionString);
    });

    AnsiConsole.MarkupLine($"[green]✓[/] {config.SourceSchemas.Count} tablas encontradas en origen.");
    AnsiConsole.WriteLine();

    // 2. Resolver selección de tablas: appsettings ("SelectedTableNames") o checklist interactivo
    List<string> selectedNames;
    if (config.SelectedTableNames != null)
    {
        selectedNames = config.SelectedTableNames;
        AnsiConsole.MarkupLine("[grey]Usando selección definida en appsettings.json (SelectedTableNames).[/]");
    }
    else
    {
        var selector = serviceProvider.GetRequiredService<ITableSelector>();
        selectedNames = selector.SelectTablesToMigrate(config.SourceSchemas, config.AlwaysExcludedTables, preselected: null);
    }

    config.TablesToMigrate = discoveryService.BuildMigrationPlan(
        config.SourceSchemas, selectedNames, config.AlwaysExcludedTables);

    // 3. Análisis de viabilidad: qué va a pasar en cada tabla antes de tocar nada
    var feasibilityService = serviceProvider.GetRequiredService<IMigrationFeasibilityService>();
    List<TableFeasibility> feasibility = new();

    await AnsiConsole.Status().StartAsync("Analizando viabilidad de la migración...", async _ =>
    {
        feasibility = await feasibilityService.AnalyzeAsync(
            sourceDialect, config.SourceConnectionString,
            targetDialect, config.TargetConnectionString,
            config.TablesToMigrate, config.SourceSchemas);
    });

    PrintFeasibilityReport(feasibility);

    if (feasibility.Any(f => f.Level == FeasibilityLevel.Risk))
    {
        AnsiConsole.MarkupLine("[bold red]Se detectaron riesgos que impiden continuar de forma segura.[/]");
        if (!AnsiConsole.Confirm("¿Forzar la migración de todas formas? (no recomendado)", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[yellow]Migración cancelada.[/]");
            Environment.Exit(1);
            return;
        }
    }
    else if (!config.DryRun && config.ConfirmBeforeTruncate)
    {
        AnsiConsole.MarkupLine("[yellow]Recomendación: haz un backup de la base de datos destino antes de continuar.[/]");
        if (!AnsiConsole.Confirm("¿Continuar con la migración?", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[yellow]Migración cancelada por el usuario.[/]");
            Environment.Exit(1);
            return;
        }
    }

    // 4. Ejecutar migración
    var migrationService = serviceProvider.GetRequiredService<IMigrationService>();
    var report = await migrationService.ExecuteMigrationAsync(config);

    Console.WriteLine();
    if (report.FailedTables == 0)
    {
        AnsiConsole.MarkupLine("[bold green]✓ MIGRACIÓN COMPLETADA EXITOSAMENTE[/]");
    }
    else
    {
        AnsiConsole.MarkupLine("[bold yellow]⚠ MIGRACIÓN COMPLETADA CON ERRORES[/]");
    }

    Console.WriteLine($"  Tablas exitosas: {report.SuccessfulTables}/{report.TotalTables}");
    Console.WriteLine($"  Total filas:     {report.TotalRowsMigrated:N0}");
    Console.WriteLine($"  Duración:        {report.TotalDuration:hh\\:mm\\:ss}");
    Console.WriteLine();

    Environment.Exit(report.FailedTables == 0 ? 0 : 1);
}
catch (Exception ex)
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold red]ERROR FATAL[/]");
    AnsiConsole.WriteException(ex);
    Environment.Exit(1);
}

static void PrintFeasibilityReport(List<TableFeasibility> feasibility)
{
    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn("Tabla");
    table.AddColumn("Modo");
    table.AddColumn("Filas origen");
    table.AddColumn("Filas destino");
    table.AddColumn("Notas");

    foreach (var f in feasibility)
    {
        var color = f.Level switch
        {
            FeasibilityLevel.Risk => "red",
            FeasibilityLevel.Warning => "yellow",
            _ => "green"
        };

        var modo = f.CopyData ? "[green]copia datos[/]" : "[grey]solo esquema[/]";
        var notes = f.Notes.Count > 0 ? string.Join("\n", f.Notes.Select(Markup.Escape)) : "OK";

        table.AddRow(
            $"[{color}]{Markup.Escape(f.TableName)}[/]",
            modo,
            f.CopyData ? f.SourceRowCount.ToString("N0") : "-",
            f.ExistsInTarget ? f.TargetRowCount.ToString("N0") : "[grey]no existe[/]",
            notes);
    }

    AnsiConsole.Write(new Rule("[bold]Análisis de viabilidad[/]").RuleStyle("grey").LeftJustified());
    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();
}
