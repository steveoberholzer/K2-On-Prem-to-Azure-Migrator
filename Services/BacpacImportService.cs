using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace K2AzureMigrator.Services;

public class BacpacImportService
{
    public static async Task<(bool ok, string message)> TestConnectionAsync(string connectionString)
    {
        try
        {
            string version = await Task.Run(() =>
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                return conn.ServerVersion;
            });
            return (true, $"Connected — Azure SQL v{version}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task ImportAsync(
        string serverConnectionString,
        string targetDatabase,
        string bacpacPath,
        bool dropIfExists,
        IProgress<string> log,
        IProgress<string>? phaseProgress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            void Log(string msg) => log.Report($"[{DateTime.Now:HH:mm:ss}] {msg}");

            var server = new SqlConnectionStringBuilder(serverConnectionString).DataSource;
            var fileMb = new FileInfo(bacpacPath).Length / (1024.0 * 1024.0);
            Log($"Source BACPAC : {bacpacPath} ({fileMb:F1} MB)");
            Log($"Target        : [{targetDatabase}] on {server}");

            if (dropIfExists)
            {
                using var conn = new SqlConnection(serverConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{targetDatabase.Replace("'", "''")}') DROP DATABASE [{targetDatabase}]";
                cmd.CommandTimeout = 120;
                Log($"Dropping existing [{targetDatabase}] if present…");
                cmd.ExecuteNonQuery();
                Log("  Done.");
            }

            using var bacpac = BacPackage.Load(bacpacPath);
            var dacServices = new DacServices(serverConnectionString);
            dacServices.Message += (_, e) => Log($"  [DacFx] {e.Message}");
            dacServices.ProgressChanged += (_, e) =>
            {
                Log($"  [DacFx] {e.Status}: {e.Message}");
                if (e.Status.ToString() == "Running")
                    phaseProgress?.Report(e.Message);
            };

            ct.ThrowIfCancellationRequested();
            Log("Import started.");

            var startTime = DateTime.Now;
            try
            {
                dacServices.ImportBacpac(bacpac, targetDatabase, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                // Walk the full inner-exception chain so the operator sees the real SQL error,
                // not just the top-level "Could not import package" wrapper.
                var e = ex;
                while (e != null)
                {
                    Log($"  ERROR: {e.Message}");
                    e = e.InnerException;
                }
                throw;
            }

            var duration = DateTime.Now - startTime;
            Log("Import finished.");
            Log($"  Duration : {(int)duration.TotalMinutes}m {duration.Seconds:D2}s");
            Log($"  Database : [{targetDatabase}] is live on {server}");
        }, ct);
    }
}
