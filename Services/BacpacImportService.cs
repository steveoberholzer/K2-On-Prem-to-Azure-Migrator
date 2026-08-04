using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace K2AzureMigrator.Services;

public class BacpacImportService
{
    public Task ImportAsync(
        string serverConnectionString,
        string targetDatabase,
        string bacpacPath,
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
            dacServices.ImportBacpac(bacpac, targetDatabase, cancellationToken: ct);

            var duration = DateTime.Now - startTime;
            Log("Import finished.");
            Log($"  Duration : {(int)duration.TotalMinutes}m {duration.Seconds:D2}s");
            Log($"  Database : [{targetDatabase}] is live on {server}");
        }, ct);
    }
}
