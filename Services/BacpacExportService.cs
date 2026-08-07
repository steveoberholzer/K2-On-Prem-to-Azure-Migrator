using System.IO;
using System.Text.RegularExpressions;
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

            // Azure SQL does not support Windows-authenticated users. DacFx will refuse to
            // package them, so we drop them before export. They must be re-created in Azure AD
            // after migration — they cannot be carried across as-is.
            DropWindowsUsers(connectionString, Log);

            // Procedures that reference named databases (e.g. ALTER DATABASE [tempdb]) fail
            // to CREATE in Azure SQL. Replace their bodies with a stub so the BACPAC imports
            // cleanly; the signature is preserved so any K2 caller gets a silent no-op.
            StubAzureIncompatibleProcedures(connectionString, Log);

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

        // Transfer schema ownership to dbo before dropping users
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

        log("  Windows users removed — export can now proceed.");
    }

    private static void StubAzureIncompatibleProcedures(string connectionString, Action<string> log)
    {
        // Read the original definition from OBJECT_DEFINITION — no parameter reconstruction.
        // We swap CREATE→ALTER and replace everything from AS…BEGIN onwards with a no-op body.
        // This avoids all parameter-type reconstruction issues.
        const string findSql = @"
            SELECT SCHEMA_NAME(o.schema_id) AS [Schema], o.name,
                   OBJECT_DEFINITION(o.object_id) AS Definition
            FROM sys.objects o
            WHERE o.type = 'P'
              AND OBJECT_DEFINITION(o.object_id) LIKE '%ALTER DATABASE%'
            ORDER BY SCHEMA_NAME(o.schema_id), o.name";

        using var conn = new SqlConnection(connectionString);
        conn.Open();

        var procs = new List<(string schema, string name, string def)>();
        using (var cmd = new SqlCommand(findSql, conn))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                procs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        if (procs.Count == 0) return;

        log($"Stubbing {procs.Count} procedure(s) with Azure SQL-incompatible ALTER DATABASE statements:");

        foreach (var (schema, name, def) in procs)
        {
            log($"  [{schema}].[{name}]");

            // 1. Flip CREATE → ALTER so the statement updates the existing object.
            var altered = Regex.Replace(
                def,
                @"\bCREATE\s+PROCEDURE\b",
                "ALTER PROCEDURE",
                RegexOptions.IgnoreCase);

            // 2. Find where the body starts: AS followed by whitespace then BEGIN.
            //    \s+ spans newlines, so it matches both "AS BEGIN" and "AS\r\nBEGIN".
            var bodyMatch = Regex.Match(
                altered,
                @"\bAS\b\s+BEGIN\b",
                RegexOptions.IgnoreCase);

            if (!bodyMatch.Success)
            {
                // Fallback: drop the procedure if we can't locate the body boundary.
                log($"    (body boundary not found — dropping procedure instead)");
                using var drop = new SqlCommand(
                    $"DROP PROCEDURE IF EXISTS [{schema}].[{name}]", conn);
                drop.ExecuteNonQuery();
                continue;
            }

            // 3. Keep everything up to (not including) AS…BEGIN, then write a no-op body.
            var stub = altered[..bodyMatch.Index] +
                       "\r\nAS\r\nBEGIN\r\n" +
                       "    -- Stubbed: body contained ALTER DATABASE statements unsupported in Azure SQL.\r\n" +
                       "END";

            using var alterCmd = new SqlCommand(stub, conn);
            alterCmd.ExecuteNonQuery();
        }

        log("  Procedures stubbed — export can proceed.");
    }
}
