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

            DropWindowsUsers(connectionString, Log);
            DropAzureIncompatibleProcedures(connectionString, Log);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            var dacServices = new DacServices(connectionString);
            dacServices.Message += (_, e) => Log($"  [DacFx] {e.Message}");
            dacServices.ProgressChanged += (_, e) =>
            {
                Log($"  [DacFx] {e.Status}: {e.Message}");
                if (e.Status.ToString() == "Running")
                    phaseProgress?.Report(e.Message);
            };

            Log($"Export started.");
            ct.ThrowIfCancellationRequested();
            var startTime = DateTime.Now;

            dacServices.ExportBacpac(destPath, dbName, cancellationToken: ct);

            var duration = DateTime.Now - startTime;
            var fileMb = new FileInfo(destPath).Length / (1024.0 * 1024.0);
            Log($"Export finished.");
            Log($"  Duration  : {(int)duration.TotalMinutes}m {duration.Seconds:D2}s");
            Log($"  BACPAC    : {fileMb:F1} MB  →  {destPath}");
        }, ct);
    }

    private static void DropWindowsUsers(string connectionString, Action<string> log)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();

        using var findCmd = conn.CreateCommand();
        findCmd.CommandText = @"
            SELECT name FROM sys.database_principals
            WHERE type IN ('U', 'G')
              AND name NOT IN ('dbo', 'guest', 'INFORMATION_SCHEMA', 'sys')
            ORDER BY name";

        var users = new List<string>();
        using (var reader = findCmd.ExecuteReader())
            while (reader.Read())
                users.Add(reader.GetString(0));

        if (users.Count == 0) return;

        log($"Removing {users.Count} Windows-authenticated user(s) — not supported in Azure SQL:");
        foreach (var u in users)
            log($"  [{u}] (re-create in Azure AD after migration)");

        using var xferCmd = conn.CreateCommand();
        xferCmd.CommandText = @"
            DECLARE @sql NVARCHAR(MAX) = '';
            SELECT @sql += 'ALTER AUTHORIZATION ON SCHEMA::[' + s.name + '] TO [dbo];' + CHAR(13)
            FROM sys.schemas s
            JOIN sys.database_principals dp ON s.principal_id = dp.principal_id
            WHERE dp.type IN ('U','G')
              AND dp.name NOT IN ('dbo','guest','INFORMATION_SCHEMA','sys');
            IF LEN(@sql) > 0 EXEC sp_executesql @sql;";
        xferCmd.ExecuteNonQuery();

        using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = @"
            DECLARE @sql NVARCHAR(MAX) = '';
            SELECT @sql += 'DROP USER [' + name + '];' + CHAR(13)
            FROM sys.database_principals
            WHERE type IN ('U','G')
              AND name NOT IN ('dbo','guest','INFORMATION_SCHEMA','sys')
            ORDER BY name;
            IF LEN(@sql) > 0 EXEC sp_executesql @sql;";
        dropCmd.ExecuteNonQuery();

        log("  Windows users removed.");
    }

    private static void DropAzureIncompatibleProcedures(string connectionString, Action<string> log)
    {
        // Procedures containing ALTER DATABASE cannot be created in Azure SQL.
        // These are on-prem DBA maintenance utilities — Azure SQL handles the equivalent
        // tasks automatically, so dropping them is safe.
        using var conn = new SqlConnection(connectionString);
        conn.Open();

        // Find them first so we can log each name.
        using var findCmd = conn.CreateCommand();
        findCmd.CommandText = @"
            SELECT SCHEMA_NAME(o.schema_id) AS [Schema], o.name
            FROM sys.objects o
            WHERE o.type = 'P'
              AND OBJECT_DEFINITION(o.object_id) LIKE '%ALTER DATABASE%'
            ORDER BY SCHEMA_NAME(o.schema_id), o.name";

        var procs = new List<string>();
        using (var reader = findCmd.ExecuteReader())
            while (reader.Read())
                procs.Add($"[{reader.GetString(0)}].[{reader.GetString(1)}]");

        if (procs.Count == 0) return;

        log($"Dropping {procs.Count} procedure(s) incompatible with Azure SQL (contain ALTER DATABASE):");
        foreach (var p in procs)
            log($"  {p}");

        // Drop them all in one batch via dynamic SQL.
        using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = @"
            DECLARE @sql NVARCHAR(MAX) = '';
            SELECT @sql += 'DROP PROCEDURE IF EXISTS [' + SCHEMA_NAME(o.schema_id) + '].[' + o.name + '];' + CHAR(13)
            FROM sys.objects o
            WHERE o.type = 'P'
              AND OBJECT_DEFINITION(o.object_id) LIKE '%ALTER DATABASE%';
            IF LEN(@sql) > 0 EXEC sp_executesql @sql;";
        dropCmd.ExecuteNonQuery();

        log("  Done.");
    }
}
