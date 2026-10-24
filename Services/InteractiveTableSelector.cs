using DBMigrationTool.Models;
using Spectre.Console;

namespace DBMigrationTool.Services;

public interface ITableSelector
{
    /// <summary>
    /// Muestra un checklist interactivo (flechas + espacio) para elegir qué tablas
    /// copian datos. Las tablas en alwaysExcludedTables ni siquiera se muestran:
    /// siempre quedan solo-esquema.
    /// </summary>
    List<string> SelectTablesToMigrate(
        List<TableSchema> discoveredTables,
        List<string> alwaysExcludedTables,
        List<string>? preselected);
}

public class InteractiveTableSelector : ITableSelector
{
    public List<string> SelectTablesToMigrate(
        List<TableSchema> discoveredTables,
        List<string> alwaysExcludedTables,
        List<string>? preselected)
    {
        var excludedSet = new HashSet<string>(alwaysExcludedTables, StringComparer.OrdinalIgnoreCase);
        var selectableTables = discoveredTables
            .Where(t => !excludedSet.Contains(t.TableName))
            .Select(t => t.TableName)
            .ToList();

        if (selectableTables.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No hay tablas disponibles para seleccionar (todas están excluidas).[/]");
            return new List<string>();
        }

        if (excludedSet.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Excluidas permanentemente ({excludedSet.Count}): {string.Join(", ", excludedSet)}[/]");
            AnsiConsole.WriteLine();
        }

        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("¿Qué tablas quieres [green]migrar con datos[/]?")
                .AddChoices("Todas las tablas", "Elegir manualmente", "Ninguna (todo solo-esquema, sin datos)"));

        List<string> selected;

        switch (mode)
        {
            case "Todas las tablas":
                selected = selectableTables;
                break;

            case "Ninguna (todo solo-esquema, sin datos)":
                selected = new List<string>();
                break;

            default:
                var prompt = new MultiSelectionPrompt<string>()
                    .Title("Selecciona las tablas que quieres [green]migrar con datos[/] (las demás se crean [grey]vacías[/]):")
                    .NotRequired()
                    .PageSize(20)
                    .MoreChoicesText("[grey](mueve arriba/abajo para ver más tablas)[/]")
                    .InstructionsText("[grey](<space> marca/desmarca, <enter> confirma)[/]")
                    .AddChoices(selectableTables);

                if (preselected != null)
                {
                    foreach (var table in preselected.Where(selectableTables.Contains))
                    {
                        prompt.Select(table);
                    }
                }

                selected = AnsiConsole.Prompt(prompt);
                break;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Tablas con datos ({selected.Count}):[/] {string.Join(", ", selected)}");

        var skipped = selectableTables.Except(selected, StringComparer.OrdinalIgnoreCase).ToList();
        if (skipped.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Tablas solo-esquema ({skipped.Count}):[/] {string.Join(", ", skipped)}");
        }

        AnsiConsole.WriteLine();

        return selected;
    }
}
