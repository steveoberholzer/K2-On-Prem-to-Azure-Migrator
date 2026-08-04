using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace K2AzureMigrator.Services;

public class BacpacExportService
{
    public Task ExportAsync(
        string connectionString,
        string destPath,
        IProgress<string> log,
        IProgress<string>? phaseProgress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            void Log(string msg) => log.Report($"[{DateTime.Now:HH:mm:ss}] {msg}");

            string dbName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

            // Log the source database data size so the operator has a baseline.
            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = new SqlCommand(
                    "SELECT CAST(SUM(size) * 8.0 / 1024 AS DECIMAL(10,1)) " +
                    "FROM sys.master_files " +
                    "WHERE database_id = DB_ID() AND type_desc = 'ROWS'", conn);
                var sizeMb = cmd.ExecuteScalar();
                Log($"Source database '{dbName}' — data size: {sizeMb} MB");
            }
            catch (Exception ex)
            {
                Log($"  (could not read database size: {ex.Message})");
            }

            Log($"Destination: {destPath}");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            var dacServices = new DacServices(connectionString);
            dacServices.Message += (_, e) => Log($"  [DacFx] {e.Message}");
            dacServices.ProgressChanged += (_, e) =>
            {
                Log($"  [DacFx] {e.Status}: {e.Message}");
                if (e.Status.ToString() == "Running")
                    phaseProgress?.Report(e.Message);
            };

            var startTime = DateTime.Now;
            Log($"Export started.");
            ct.ThrowIfCancellationRequested();

            dacServices.ExportBacpac(destPath, dbName, cancellationToken: ct);

            var duration = DateTime.Now - startTime;
            var fileMb = new FileInfo(destPath).Length / (1024.0 * 1024.0);
            Log($"Export finished.");
            Log($"  Duration  : {(int)duration.TotalMinutes}m {duration.Seconds:D2}s");
            Log($"  BACPAC    : {fileMb:F1} MB  →  {destPath}");
        }, ct);
    }
}
