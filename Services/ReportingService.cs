using System.Text;
using Microsoft.Extensions.Logging;
using DBMigrationTool.Models;

namespace DBMigrationTool.Services;

public interface IReportingService
{
    void LogTableStart(string tableName);
    void LogTableComplete(MigrationResult result);
    string GenerateFinalReport(MigrationReport report);
}

public class ReportingService : IReportingService
{
    private readonly ILogger<ReportingService> _logger;

    public ReportingService(ILogger<ReportingService> logger)
    {
        _logger = logger;
    }

    public void LogTableStart(string tableName)
    {
        _logger.LogInformation("┌─ Iniciando migración de: {TableName}", tableName);
    }

    public void LogTableComplete(MigrationResult result)
    {
        if (result.Success)
        {
            _logger.LogInformation(
                "└─ ✓ {TableName}: {RowsInserted} filas migradas en {Duration}",
                result.TableName,
                result.RowsInserted,
                result.Duration.ToString(@"mm\:ss"));
        }
        else
        {
            _logger.LogError(
                "└─ ✗ {TableName}: Falló ({ErrorCount} errores)",
                result.TableName,
                result.Errors.Count);
        }

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("   ⚠ {Warning}", warning);
        }
    }

    public string GenerateFinalReport(MigrationReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine("     REPORTE DE MIGRACIÓN DE DATOS");
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine();

        if (report.DryRunMode)
        {
            sb.AppendLine("⚠ MODO DRY-RUN: No se realizaron cambios en la base de datos");
            sb.AppendLine();
        }

        sb.AppendLine("RESUMEN:");
        sb.AppendLine($"  Inicio:              {report.StartTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Fin:                 {report.EndTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Duración Total:      {report.TotalDuration:hh\\:mm\\:ss}");
        sb.AppendLine($"  Tablas Procesadas:   {report.TotalTables}");
        sb.AppendLine($"  Tablas Exitosas:     {report.SuccessfulTables}");
        sb.AppendLine($"  Tablas Fallidas:     {report.FailedTables}");
        sb.AppendLine($"  Total Filas:         {report.TotalRowsMigrated:N0}");
        sb.AppendLine();

        sb.AppendLine("───────────────────────────────────────────────────────");
        sb.AppendLine("DETALLE POR TABLA:");
        sb.AppendLine("───────────────────────────────────────────────────────");
        sb.AppendLine();

        foreach (var tableResult in report.TableResults)
        {
            var icon = tableResult.Success ? "✓" : "✗";
            sb.AppendLine($"[{icon}] {tableResult.TableName}");
            sb.AppendLine($"    Filas Migradas:  {tableResult.RowsInserted:N0}");
            sb.AppendLine($"    Duración:        {tableResult.Duration:mm\\:ss}");

            if (tableResult.Warnings.Any())
            {
                sb.AppendLine($"    Warnings:        {tableResult.Warnings.Count}");
                foreach (var warning in tableResult.Warnings)
                {
                    sb.AppendLine($"      ⚠ {warning}");
                }
            }

            if (tableResult.Errors.Any())
            {
                sb.AppendLine($"    Errores:         {tableResult.Errors.Count}");
                foreach (var error in tableResult.Errors)
                {
                    sb.AppendLine($"      ✗ {error}");
                }
            }

            sb.AppendLine();
        }

        var allWarnings = report.TableResults.SelectMany(r => r.Warnings).Distinct().ToList();
        if (allWarnings.Any())
        {
            sb.AppendLine("───────────────────────────────────────────────────────");
            sb.AppendLine("ADVERTENCIAS DE ESQUEMA:");
            sb.AppendLine("───────────────────────────────────────────────────────");
            sb.AppendLine();

            foreach (var warning in allWarnings)
            {
                sb.AppendLine($"  - {warning}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("═══════════════════════════════════════════════════════");

        return sb.ToString();
    }
}
