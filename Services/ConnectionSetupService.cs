using DBMigrationTool.Models;
using DBMigrationTool.Utilities;
using Spectre.Console;

namespace DBMigrationTool.Services;

public class ConfiguredConnection
{
    public DatabaseEngine Engine { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
}

public interface IConnectionSetupService
{
    /// <summary>
    /// Resuelve engine + connection string para un extremo (origen/destino): parte de los
    /// valores de appsettings, deja confirmarlos o cambiarlos interactivamente, prueba la
    /// conexión y, si falla, permite reintroducirla a mano (motor y/o cadena) en vez de
    /// reventar el programa.
    /// </summary>
    Task<ConfiguredConnection> ResolveConnectionAsync(
        string label,
        DatabaseEngine defaultEngine,
        string? defaultConnectionString);
}

public class ConnectionSetupService : IConnectionSetupService
{
    public async Task<ConfiguredConnection> ResolveConnectionAsync(
        string label,
        DatabaseEngine defaultEngine,
        string? defaultConnectionString)
    {
        var engine = defaultEngine;
        var connectionString = defaultConnectionString ?? string.Empty;

        AnsiConsole.Write(new Rule($"[bold]Conexión: {label}[/]").RuleStyle("grey").LeftJustified());

        var hasDefault = !string.IsNullOrWhiteSpace(connectionString);
        var useDefault = hasDefault && AnsiConsole.Confirm(
            $"[grey]Motor:[/] [green]{engine}[/]  [grey]Cadena:[/] [grey]{Mask(connectionString)}[/]\n¿Usar esta configuración para {label}?",
            defaultValue: true);

        if (!hasDefault || !useDefault)
        {
            engine = AnsiConsole.Prompt(
                new SelectionPrompt<DatabaseEngine>()
                    .Title($"Elige el motor de base de datos para [bold]{label}[/]:")
                    .AddChoices(DatabaseEngine.SqlServer, DatabaseEngine.PostgreSql, DatabaseEngine.MySql));

            connectionString = AnsiConsole.Ask<string>($"Cadena de conexión para [bold]{label}[/]:");
        }

        while (true)
        {
            var dialect = DatabaseDialectFactory.Create(engine);

            var (success, error) = await TestConnectionAsync(dialect, connectionString);
            if (success)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Conexión a {label} exitosa ({engine}).");
                AnsiConsole.WriteLine();
                return new ConfiguredConnection { Engine = engine, ConnectionString = connectionString };
            }

            AnsiConsole.MarkupLine($"[red]✗ No se pudo conectar a {label}:[/] {Markup.Escape(error ?? "error desconocido")}");

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("¿Qué deseas hacer?")
                    .AddChoices("Reintroducir la cadena de conexión", "Cambiar el motor de base de datos", "Reintentar igual", "Abortar"));

            switch (action)
            {
                case "Reintroducir la cadena de conexión":
                    connectionString = AnsiConsole.Ask<string>($"Nueva cadena de conexión para [bold]{label}[/]:");
                    break;
                case "Cambiar el motor de base de datos":
                    engine = AnsiConsole.Prompt(
                        new SelectionPrompt<DatabaseEngine>()
                            .Title($"Elige el motor de base de datos para [bold]{label}[/]:")
                            .AddChoices(DatabaseEngine.SqlServer, DatabaseEngine.PostgreSql, DatabaseEngine.MySql));
                    connectionString = AnsiConsole.Ask<string>($"Cadena de conexión para [bold]{label}[/]:");
                    break;
                case "Abortar":
                    throw new OperationCanceledException($"Configuración de conexión a {label} cancelada por el usuario.");
                // "Reintentar igual" simplemente vuelve a probar la misma cadena
            }
        }
    }

    private static async Task<(bool Success, string? Error)> TestConnectionAsync(IDatabaseDialect dialect, string connectionString)
    {
        try
        {
            using var connection = dialect.CreateConnection(connectionString);
            await connection.OpenAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Mask(string connectionString)
    {
        // Oculta password=... / pwd=... para no imprimir credenciales en pantalla
        return System.Text.RegularExpressions.Regex.Replace(
            connectionString,
            @"(?i)(password|pwd)\s*=\s*[^;]*",
            "$1=****");
    }
}
