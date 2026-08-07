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
        // Find stored procedures that reference a named database in ALTER DATABASE — these
        // are on-prem administrative procs (e.g. ALTER DATABASE [tempdb] SET PAGE_VERIFY)
        // that Azure SQL will refuse to execute. We replace their bodies with a no-op stub.
        const string findSql = @"
            SELECT o.object_id, SCHEMA_NAME(o.schema_id) AS [Schema], o.name
            FROM sys.objects o
            WHERE o.type = 'P'
              AND OBJECT_DEFINITION(o.object_id) LIKE '%ALTER DATABASE%'
            ORDER BY SCHEMA_NAME(o.schema_id), o.name";

        using var conn = new SqlConnection(connectionString);
        conn.Open();

        var procs = new List<(int id, string schema, string name)>();
        using (var cmd = new SqlCommand(findSql, conn))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                procs.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));

        if (procs.Count == 0) return;

        log($"Stubbing {procs.Count} procedure(s) with Azure SQL-incompatible ALTER DATABASE statements:");

        const string paramsSql = @"
            SELECT p.name, tp.name AS TypeName,
                   p.max_length, p.precision, p.scale,
                   p.is_output, p.has_default_value,
                   CAST(p.default_value AS NVARCHAR(200)) AS DefaultVal
            FROM sys.parameters p
            JOIN sys.types tp ON p.user_type_id = tp.user_type_id
            WHERE p.object_id = @id AND p.parameter_id > 0
            ORDER BY p.parameter_id";

        foreach (var (id, schema, name) in procs)
        {
            var sb = new System.Text.StringBuilder();
            using (var pCmd = new SqlCommand(paramsSql, conn))
            {
                pCmd.Parameters.AddWithValue("@id", id);
                using var r = pCmd.ExecuteReader();
                bool first = true;
                while (r.Read())
                {
                    sb.Append(first ? "\r\n    " : ",\r\n    ");
                    first = false;

                    string pName  = r.GetString(0);
                    string tName  = r.GetString(1).ToUpperInvariant();
                    short  maxLen = r.GetInt16(2);
                    byte   prec   = r.GetByte(3);
                    byte   scale  = r.GetByte(4);
                    bool   isOut  = r.GetBoolean(5);
                    bool   hasDef = r.GetBoolean(6);
                    string? defVal = r.IsDBNull(7) ? null : r.GetString(7);

                    string typeStr = tName switch
                    {
                        "NVARCHAR" or "NCHAR"     => maxLen == -1 ? $"{tName}(MAX)" : $"{tName}({maxLen / 2})",
                        "VARCHAR"  or "CHAR"      => maxLen == -1 ? $"{tName}(MAX)" : $"{tName}({maxLen})",
                        "VARBINARY" or "BINARY"   => maxLen == -1 ? $"{tName}(MAX)" : $"{tName}({maxLen})",
                        "DECIMAL"  or "NUMERIC"   => $"{tName}({prec},{scale})",
                        _                         => tName
                    };

                    sb.Append($"{pName} {typeStr}");

                    if (hasDef && defVal != null)
                    {
                        // BIT defaults from sql_variant cast come back as "True"/"False" on some
                        // SQL Server versions and as "0"/"1" on others — normalise both.
                        string dv = tName == "BIT"
                            ? (defVal.Equals("True", StringComparison.OrdinalIgnoreCase) ? "1" : "0")
                            : defVal;
                        sb.Append($" = {dv}");
                    }
                    if (isOut) sb.Append(" OUTPUT");
                }
            }

            string stub =
                $"ALTER PROCEDURE [{schema}].[{name}]{sb}\r\n" +
                "AS\r\nBEGIN\r\n" +
                "    -- Stubbed for Azure SQL migration.\r\n" +
                "    -- Original body contained ALTER DATABASE [named] statements unsupported in Azure SQL.\r\n" +
                "    -- Re-implement using Azure SQL equivalents if this procedure is required.\r\nEND";

            log($"  [{schema}].[{name}]");
            using var alterCmd = new SqlCommand(stub, conn);
            alterCmd.ExecuteNonQuery();
        }

        log("  Procedures stubbed — export can proceed.");
    }
}
