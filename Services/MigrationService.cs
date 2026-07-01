using System.Data;
using Microsoft.Data.SqlClient;

namespace K2AzureMigrator.Services;

public enum CheckStatus { Pending, Running, Pass, Fail, Warning }

public class PreFlightCheck
{
    public string Name { get; set; } = "";
    public CheckStatus Status { get; set; } = CheckStatus.Pending;
    public string Detail { get; set; } = "";
    public bool IsWarningOnly { get; set; }
}

public class SmartboxColumn
{
    public string TableName { get; set; } = "";
    public string ColumnName { get; set; } = "";
    public int RowCount { get; set; }
}

public class DiscoveryResult
{
    public int ConfigEncryptedCount { get; set; }
    public List<string> ConfigTokens { get; set; } = [];
    public List<SmartboxColumn> SmartboxColumns { get; set; } = [];
    public bool RecentBackupFound { get; set; }
    public string BackupAge { get; set; } = "";
    public int ActiveK2Sessions { get; set; }
}

public class MigrationResult
{
    public bool Success { get; set; }
    public bool IsDryRun { get; set; }
    public int ConfigRowsDecrypted { get; set; }
    public List<string> SmartboxColumnsDecrypted { get; set; } = [];
    public bool UseSqlEncryptionCleared { get; set; }
    public bool CryptoObjectsDropped { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

public class MigrationService
{
    public const string DefaultMasterKeyPassword = "5CE05F96-98A1-475C-9E8C-5053F057D312";
    public const string SymmetricKeyName = "SCSSOKey";
    public const string CertificateName = "SCHostServerCert";

    public static string BuildConnectionString(string server, string database, bool trustedAuth, string username, string password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(server) ? "." : server,
            InitialCatalog = string.IsNullOrWhiteSpace(database) ? "K2" : database,
            ConnectTimeout = 10,
            TrustServerCertificate = true
        };
        if (trustedAuth)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = username;
            b.Password = password;
        }
        return b.ConnectionString;
    }

    public async Task<(bool ok, string message)> TestConnectionAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return (true, $"Connected to {conn.DataSource} / {conn.Database}");
        }
        catch (Exception ex)
        {
            Logger.LogError("TestConnectionAsync", ex);
            const int maxLen = 100;
            string shown = ex.Message.Length > maxLen
                ? ex.Message[..maxLen].TrimEnd() + "… (see log)"
                : ex.Message;
            return (false, shown);
        }
    }

    public async Task RunPreFlightChecksAsync(
        string connectionString,
        string masterKeyPassword,
        List<PreFlightCheck> checks,
        Action<PreFlightCheck> onCheckUpdated,
        CancellationToken ct = default)
    {
        checks.Clear();
        var list = new List<PreFlightCheck>
        {
            new() { Name = "SQL Connectivity" },
            new() { Name = "K2 Schema Present" },
            new() { Name = "SQL Encryption Active" },
            new() { Name = "Symmetric Key Exists (SCSSOKey)" },
            new() { Name = "Certificate Exists (SCHostServerCert)" },
            new() { Name = "Master Key Openable" },
            new() { Name = "Symmetric Key Openable" },
            new() { Name = "Decryption Test" },
            new() { Name = "Recent Database Backup", IsWarningOnly = true },
            new() { Name = "K2 Services Stopped", IsWarningOnly = true },
        };
        foreach (var c in list) { checks.Add(c); onCheckUpdated(c); }

        SqlConnection? conn = null;
        bool masterKeyOpen = false;
        bool symKeyOpen = false;

        try
        {
            // 1. Connectivity
            Update(list[0], CheckStatus.Running);
            try
            {
                conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct);
                Update(list[0], CheckStatus.Pass, $"Connected to {conn.DataSource} / {conn.Database}");
            }
            catch (Exception ex)
            {
                Update(list[0], CheckStatus.Fail, ex.Message);
                FailRemaining(list, 1);
                return;
            }

            // 2. K2 Schema
            Update(list[1], CheckStatus.Running);
            bool schemaOk = await ScalarBoolAsync(conn,
                "SELECT CASE WHEN OBJECT_ID('[HostServer].[Configuration]','U') IS NOT NULL THEN 1 ELSE 0 END");
            if (!schemaOk)
            {
                Update(list[1], CheckStatus.Fail, "[HostServer].[Configuration] table not found — not a K2 database");
                FailRemaining(list, 2);
                return;
            }
            Update(list[1], CheckStatus.Pass, "[HostServer].[Configuration] exists");

            // 3. SQL Encryption Active
            Update(list[2], CheckStatus.Running);
            string? useSqlEnc = await ScalarStringAsync(conn,
                "SELECT [VariableValue] FROM [HostServer].[Configuration] WHERE [VariableToken] = '[USESQLENCRYPTION]'");
            if (!string.Equals(useSqlEnc, "True", StringComparison.OrdinalIgnoreCase))
            {
                Update(list[2], CheckStatus.Fail,
                    string.IsNullOrEmpty(useSqlEnc)
                        ? "[USESQLENCRYPTION] row not found"
                        : $"[USESQLENCRYPTION] = '{useSqlEnc}' — SQL encryption not active; nothing to do");
                FailRemaining(list, 3);
                return;
            }
            Update(list[2], CheckStatus.Pass, "[USESQLENCRYPTION] = True");

            // 4. Symmetric key exists
            Update(list[3], CheckStatus.Running);
            bool keyExists = await ScalarBoolAsync(conn,
                $"SELECT CASE WHEN EXISTS(SELECT 1 FROM sys.symmetric_keys WHERE name = '{SymmetricKeyName}') THEN 1 ELSE 0 END");
            if (!keyExists)
            {
                Update(list[3], CheckStatus.Fail, $"Symmetric key '{SymmetricKeyName}' not found in sys.symmetric_keys");
                FailRemaining(list, 4);
                return;
            }
            Update(list[3], CheckStatus.Pass, $"'{SymmetricKeyName}' found");

            // 5. Certificate exists
            Update(list[4], CheckStatus.Running);
            string? certExpiry = await ScalarStringAsync(conn,
                $"SELECT CONVERT(VARCHAR(20), expiry_date, 103) FROM sys.certificates WHERE name = '{CertificateName}'");
            if (certExpiry == null)
            {
                Update(list[4], CheckStatus.Fail, $"Certificate '{CertificateName}' not found in sys.certificates");
                FailRemaining(list, 5);
                return;
            }
            bool certExpired = await ScalarBoolAsync(conn,
                $"SELECT CASE WHEN expiry_date < GETDATE() THEN 1 ELSE 0 END FROM sys.certificates WHERE name = '{CertificateName}'");
            if (certExpired)
                Update(list[4], CheckStatus.Warning, $"'{CertificateName}' exists but EXPIRED on {certExpiry} — decryption may still work");
            else
                Update(list[4], CheckStatus.Pass, $"'{CertificateName}' valid until {certExpiry}");

            // 6. Master key openable
            Update(list[5], CheckStatus.Running);
            string safePassword = masterKeyPassword.Replace("'", "''");
            try
            {
                await ExecAsync(conn, $"OPEN MASTER KEY DECRYPTION BY PASSWORD = N'{safePassword}'");
                masterKeyOpen = true;
                Update(list[5], CheckStatus.Pass, "Master key opened with supplied password");
            }
            catch (Exception ex)
            {
                Update(list[5], CheckStatus.Fail, $"Cannot open master key: {ex.Message}");
                FailRemaining(list, 6);
                return;
            }

            // 7. Symmetric key openable
            Update(list[6], CheckStatus.Running);
            try
            {
                await ExecAsync(conn, $"OPEN SYMMETRIC KEY [{SymmetricKeyName}] DECRYPTION BY CERTIFICATE [{CertificateName}]");
                symKeyOpen = true;
                Update(list[6], CheckStatus.Pass, $"'{SymmetricKeyName}' opened via '{CertificateName}'");
            }
            catch (Exception ex)
            {
                Update(list[6], CheckStatus.Fail, $"Cannot open symmetric key: {ex.Message}");
                FailRemaining(list, 7);
                return;
            }

            // 8. Decryption test
            Update(list[7], CheckStatus.Running);
            try
            {
                int totalEnc = (int)await ScalarAsync(conn,
                    "SELECT COUNT(*) FROM [HostServer].[Configuration] WHERE [Encrypted] = 1");
                int cannotDecrypt = (int)await ScalarAsync(conn,
                    "SELECT COUNT(*) FROM [HostServer].[Configuration] WHERE [Encrypted] = 1 AND CONVERT(NVARCHAR(MAX), DecryptByKey([VariableValue])) IS NULL");

                if (totalEnc == 0)
                    Update(list[7], CheckStatus.Warning, "No encrypted rows found — nothing to decrypt");
                else if (cannotDecrypt == totalEnc)
                    Update(list[7], CheckStatus.Fail, $"All {totalEnc} encrypted rows returned NULL — encryption was applied by a different server/key");
                else if (cannotDecrypt > 0)
                    Update(list[7], CheckStatus.Warning, $"{totalEnc - cannotDecrypt}/{totalEnc} rows decryptable ({cannotDecrypt} will return NULL)");
                else
                    Update(list[7], CheckStatus.Pass, $"All {totalEnc} encrypted rows are decryptable");
            }
            catch (Exception ex)
            {
                Update(list[7], CheckStatus.Fail, $"Decryption test error: {ex.Message}");
            }

            // 9. Recent backup (warning only — separate connection to msdb)
            Update(list[8], CheckStatus.Running);
            try
            {
                var dbName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
                string backupSql = $@"
                    SELECT TOP 1 backup_finish_date
                    FROM msdb.dbo.backupset
                    WHERE database_name = N'{dbName.Replace("'", "''")}' AND type = 'D'
                    ORDER BY backup_finish_date DESC";
                object? backupDate = await ScalarAsync(conn, backupSql);
                if (backupDate == null || backupDate == DBNull.Value)
                {
                    Update(list[8], CheckStatus.Warning, "No full database backup found in msdb — take a backup before proceeding");
                }
                else
                {
                    var dt = (DateTime)backupDate;
                    var age = DateTime.Now - dt;
                    string ageStr = age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago" :
                                    age.TotalHours < 24 ? $"{(int)age.TotalHours}h {age.Minutes}m ago" :
                                    $"{(int)age.TotalDays}d ago";
                    if (age.TotalHours > 24)
                        Update(list[8], CheckStatus.Warning, $"Last backup was {ageStr} — consider taking a fresh backup first");
                    else
                        Update(list[8], CheckStatus.Pass, $"Last full backup: {ageStr}");
                }
            }
            catch
            {
                Update(list[8], CheckStatus.Warning, "Could not query msdb — verify a backup exists before proceeding");
            }

            // 10. K2 services check
            Update(list[9], CheckStatus.Running);
            try
            {
                int sessionCount = (int)await ScalarAsync(conn, @"
                    SELECT COUNT(*) FROM sys.dm_exec_sessions s
                    WHERE s.login_name LIKE '%K2%' OR s.program_name LIKE '%K2%'");
                if (sessionCount > 0)
                    Update(list[9], CheckStatus.Warning, $"{sessionCount} active session(s) from K2 detected — stop K2 services before executing");
                else
                    Update(list[9], CheckStatus.Pass, "No active K2 sessions detected");
            }
            catch
            {
                Update(list[9], CheckStatus.Warning, "Could not check active sessions — ensure K2 services are stopped before executing");
            }
        }
        finally
        {
            if (conn != null)
            {
                if (symKeyOpen) try { await ExecAsync(conn, $"CLOSE SYMMETRIC KEY [{SymmetricKeyName}]"); } catch { }
                if (masterKeyOpen) try { await ExecAsync(conn, "CLOSE MASTER KEY"); } catch { }
                conn.Dispose();
            }
        }

        void Update(PreFlightCheck check, CheckStatus status, string detail = "")
        {
            check.Status = status;
            check.Detail = detail;
            onCheckUpdated(check);
        }

        void FailRemaining(List<PreFlightCheck> allChecks, int from)
        {
            for (int i = from; i < allChecks.Count; i++)
            {
                allChecks[i].Status = CheckStatus.Pending;
                allChecks[i].Detail = "Skipped";
                onCheckUpdated(allChecks[i]);
            }
        }
    }

    public async Task<DiscoveryResult> DiscoverAsync(string connectionString, CancellationToken ct = default)
    {
        var result = new DiscoveryResult();
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Encrypted config row count
        result.ConfigEncryptedCount = (int)await ScalarAsync(conn,
            "SELECT COUNT(*) FROM [HostServer].[Configuration] WHERE [Encrypted] = 1");

        // Token names
        using (var cmd = new SqlCommand(
            "SELECT [VariableToken] FROM [HostServer].[Configuration] WHERE [Encrypted] = 1 ORDER BY [VariableToken]", conn))
        using (var rdr = await cmd.ExecuteReaderAsync(ct))
            while (await rdr.ReadAsync(ct))
                result.ConfigTokens.Add(rdr.GetString(0));

        // SmartBox encrypted columns
        try
        {
            const string smartboxSql = @"
                SELECT DISTINCT
                    '[SmartBoxData].[' + E.value('../../../../../@name','NVARCHAR(128)') + ']' AS TableName,
                    '['              + E.value('../../../@name',         'NVARCHAR(128)') + ']' AS ColumnName
                FROM [Smartbox].[SmartboxObject] AS SO
                CROSS APPLY [SmartboxObjectXml].nodes('object/properties/property/metadata/service/key') AS K(E)
                WHERE E.value('@name','NVARCHAR(50)') = 'encrypted'
                  AND E.value('.','NVARCHAR(5)')      = 'True'";

            using var cmd2 = new SqlCommand(smartboxSql, conn);
            using var rdr2 = await cmd2.ExecuteReaderAsync(ct);
            var cols = new List<SmartboxColumn>();
            while (await rdr2.ReadAsync(ct))
                cols.Add(new SmartboxColumn { TableName = rdr2.GetString(0), ColumnName = rdr2.GetString(1) });
            rdr2.Close();

            foreach (var col in cols)
            {
                string countSql = $"SELECT COUNT(*) FROM {col.TableName}";
                try { col.RowCount = (int)await ScalarAsync(conn, countSql); } catch { }
                result.SmartboxColumns.Add(col);
            }
        }
        catch { /* Smartbox schema may not be present */ }

        // Recent backup
        try
        {
            var dbName = conn.Database;
            object? bk = await ScalarAsync(conn,
                $"SELECT TOP 1 backup_finish_date FROM msdb.dbo.backupset WHERE database_name=N'{dbName.Replace("'","''")}' AND type='D' ORDER BY backup_finish_date DESC");
            if (bk is DateTime dt)
            {
                result.RecentBackupFound = true;
                var age = DateTime.Now - dt;
                result.BackupAge = age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago" :
                                   age.TotalHours < 24 ? $"{(int)age.TotalHours}h {age.Minutes}m ago" :
                                   $"{(int)age.TotalDays}d ago ({dt:dd/MM/yyyy HH:mm})";
            }
        }
        catch { }

        // Active K2 sessions
        try
        {
            result.ActiveK2Sessions = (int)await ScalarAsync(conn,
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name LIKE '%K2%' OR program_name LIKE '%K2%'");
        }
        catch { }

        return result;
    }

    public async Task<MigrationResult> ExecuteAsync(
        string connectionString,
        string masterKeyPassword,
        bool dryRun,
        bool dropCryptoObjects,
        IProgress<string> log,
        CancellationToken ct = default)
    {
        var result = new MigrationResult { IsDryRun = dryRun };

        void Log(string msg) => log.Report($"[{DateTime.Now:HH:mm:ss}] {msg}");

        Log(dryRun ? "DRY RUN — no changes will be made" : "Starting migration...");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        Log($"Connected: {conn.DataSource} / {conn.Database}");

        bool masterKeyOpen = false;
        bool symKeyOpen = false;

        try
        {
            // Open master key
            Log("Opening database master key...");
            string safePassword = masterKeyPassword.Replace("'", "''");
            await ExecAsync(conn, $"OPEN MASTER KEY DECRYPTION BY PASSWORD = N'{safePassword}'");
            masterKeyOpen = true;
            Log("Master key opened.");

            // Open symmetric key
            Log($"Opening symmetric key [{SymmetricKeyName}]...");
            await ExecAsync(conn, $"OPEN SYMMETRIC KEY [{SymmetricKeyName}] DECRYPTION BY CERTIFICATE [{CertificateName}]");
            symKeyOpen = true;
            Log("Symmetric key opened.");

            // Pre-check decryptability
            Log("Verifying all encrypted rows are decryptable...");
            int totalEnc = (int)await ScalarAsync(conn,
                "SELECT COUNT(*) FROM [HostServer].[Configuration] WHERE [Encrypted] = 1");
            int nullCount = (int)await ScalarAsync(conn,
                "SELECT COUNT(*) FROM [HostServer].[Configuration] WHERE [Encrypted] = 1 AND CONVERT(NVARCHAR(MAX), DecryptByKey([VariableValue])) IS NULL");

            Log($"Found {totalEnc} encrypted rows, {nullCount} cannot be decrypted.");

            if (nullCount == totalEnc && totalEnc > 0)
            {
                result.ErrorMessage = $"All {totalEnc} encrypted rows returned NULL — encryption was applied by a different server's key. Aborting.";
                Log($"ERROR: {result.ErrorMessage}");
                return result;
            }

            if (nullCount > 0)
            {
                string warn = $"{nullCount} row(s) could not be decrypted and will be left as-is.";
                result.Warnings.Add(warn);
                Log($"WARNING: {warn}");
            }

            // SmartBox discovery
            var smartboxCols = new List<SmartboxColumn>();
            try
            {
                const string sbSql = @"
                    SELECT DISTINCT
                        '[SmartBoxData].[' + E.value('../../../../../@name','NVARCHAR(128)') + ']',
                        '['              + E.value('../../../@name',         'NVARCHAR(128)') + ']'
                    FROM [Smartbox].[SmartboxObject] AS SO
                    CROSS APPLY [SmartboxObjectXml].nodes('object/properties/property/metadata/service/key') AS K(E)
                    WHERE E.value('@name','NVARCHAR(50)') = 'encrypted'
                      AND E.value('.','NVARCHAR(5)')      = 'True'";
                using var cmd = new SqlCommand(sbSql, conn);
                using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                    smartboxCols.Add(new SmartboxColumn { TableName = rdr.GetString(0), ColumnName = rdr.GetString(1) });
            }
            catch { }

            if (smartboxCols.Count > 0)
                Log($"Found {smartboxCols.Count} encrypted SmartBox column(s).");
            else
                Log("No encrypted SmartBox columns found.");

            if (dryRun)
            {
                Log($"DRY RUN: Would decrypt {totalEnc - nullCount} [HostServer].[Configuration] rows.");
                foreach (var col in smartboxCols)
                    Log($"DRY RUN: Would decrypt {col.TableName}.{col.ColumnName}");
                Log("DRY RUN: Would set [USESQLENCRYPTION] = False.");
                if (dropCryptoObjects)
                    Log($"DRY RUN: Would drop [{SymmetricKeyName}], [{CertificateName}], MASTER KEY.");
                result.Success = true;
                result.ConfigRowsDecrypted = totalEnc - nullCount;
                return result;
            }

            // Close key before transaction (reopen inside)
            await ExecAsync(conn, $"CLOSE SYMMETRIC KEY [{SymmetricKeyName}]");
            symKeyOpen = false;

            // Begin transaction for all data modifications
            Log("Beginning transaction...");
            using var tx = conn.BeginTransaction();
            try
            {
                // Reopen symmetric key within transaction scope
                await ExecAsync(conn, tx, $"OPEN SYMMETRIC KEY [{SymmetricKeyName}] DECRYPTION BY CERTIFICATE [{CertificateName}]");
                symKeyOpen = true;

                // Decrypt [HostServer].[Configuration]
                Log("Decrypting [HostServer].[Configuration]...");
                int rowsUpdated = await ExecNonQueryAsync(conn, tx, @"
                    UPDATE [HostServer].[Configuration]
                    SET    [VariableValue] = CONVERT(NVARCHAR(MAX), DecryptByKey([VariableValue])),
                           [Encrypted]     = 0
                    WHERE  [Encrypted] = 1
                      AND  CONVERT(NVARCHAR(MAX), DecryptByKey([VariableValue])) IS NOT NULL");
                Log($"Decrypted {rowsUpdated} configuration rows.");
                result.ConfigRowsDecrypted = rowsUpdated;

                // Decrypt SmartBox columns
                foreach (var col in smartboxCols)
                {
                    Log($"Decrypting {col.TableName}.{col.ColumnName}...");
                    try
                    {
                        int sbRows = await ExecNonQueryAsync(conn, tx,
                            $"UPDATE {col.TableName} SET {col.ColumnName} = CONVERT(NVARCHAR(MAX), DecryptByKey({col.ColumnName})) WHERE {col.ColumnName} IS NOT NULL");
                        Log($"  → {sbRows} rows decrypted.");
                        result.SmartboxColumnsDecrypted.Add($"{col.TableName}.{col.ColumnName} ({sbRows} rows)");
                    }
                    catch (Exception ex)
                    {
                        string warn = $"Could not decrypt {col.TableName}.{col.ColumnName}: {ex.Message}";
                        result.Warnings.Add(warn);
                        Log($"WARNING: {warn}");
                    }
                }

                // Close key within transaction
                await ExecAsync(conn, tx, $"CLOSE SYMMETRIC KEY [{SymmetricKeyName}]");
                symKeyOpen = false;

                // Set USESQLENCRYPTION = False
                Log("Setting [USESQLENCRYPTION] = False...");
                await ExecAsync(conn, tx, @"
                    UPDATE [HostServer].[Configuration]
                    SET    [VariableValue] = 'False'
                    WHERE  [VariableToken] = '[USESQLENCRYPTION]'");
                result.UseSqlEncryptionCleared = true;
                Log("[USESQLENCRYPTION] updated.");

                // Verify
                int remaining = (int)await ScalarAsync(conn, tx,
                    "SELECT COUNT(*) FROM [HostServer].[Configuration] WHERE [Encrypted] = 1");
                if (remaining > 0)
                    Log($"NOTE: {remaining} row(s) remain encrypted (could not be decrypted — NULL result from DecryptByKey).");

                await tx.CommitAsync(ct);
                Log("Transaction committed.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                Log($"ERROR: {ex.Message} — transaction rolled back, no changes made.");
                result.ErrorMessage = ex.Message;
                return result;
            }

            // Drop crypto objects (outside transaction — DDL)
            if (dropCryptoObjects)
            {
                Log("Dropping encryption objects...");
                try
                {
                    await ExecAsync(conn, $"DROP SYMMETRIC KEY [{SymmetricKeyName}]");
                    Log($"Dropped SYMMETRIC KEY [{SymmetricKeyName}].");
                }
                catch (Exception ex) { Log($"WARNING: Could not drop symmetric key: {ex.Message}"); }

                try
                {
                    await ExecAsync(conn, $"DROP CERTIFICATE [{CertificateName}]");
                    Log($"Dropped CERTIFICATE [{CertificateName}].");
                }
                catch (Exception ex) { Log($"WARNING: Could not drop certificate: {ex.Message}"); }

                try
                {
                    await ExecAsync(conn, "DROP MASTER KEY");
                    Log("Dropped MASTER KEY.");
                    result.CryptoObjectsDropped = true;
                }
                catch (Exception ex) { Log($"WARNING: Could not drop master key: {ex.Message}"); }
            }

            result.Success = true;
            Log(dryRun ? "Dry run complete." : "Migration complete. Database is ready for BACPAC export.");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Log($"FATAL: {ex.Message}");
        }
        finally
        {
            if (symKeyOpen) try { await ExecAsync(conn, $"CLOSE SYMMETRIC KEY [{SymmetricKeyName}]"); } catch { }
            if (masterKeyOpen) try { await ExecAsync(conn, "CLOSE MASTER KEY"); } catch { }
        }

        return result;
    }

    // ── SQL helpers ──────────────────────────────────────────────────────────

    private static async Task ExecAsync(SqlConnection conn, string sql, SqlTransaction? tx = null)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction tx, string sql)
        => await ExecAsync(conn, sql, tx);

    private static async Task<int> ExecNonQueryAsync(SqlConnection conn, SqlTransaction tx, string sql)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.CommandTimeout = 300;
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object> ScalarAsync(SqlConnection conn, string sql, SqlTransaction? tx = null)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.CommandTimeout = 60;
        return (await cmd.ExecuteScalarAsync()) ?? DBNull.Value;
    }

    private static async Task<object> ScalarAsync(SqlConnection conn, SqlTransaction tx, string sql)
        => await ScalarAsync(conn, sql, tx);

    private static async Task<bool> ScalarBoolAsync(SqlConnection conn, string sql)
    {
        var result = await ScalarAsync(conn, sql);
        return result is not DBNull && Convert.ToInt32(result) != 0;
    }

    private static async Task<string?> ScalarStringAsync(SqlConnection conn, string sql)
    {
        var result = await ScalarAsync(conn, sql);
        return result is DBNull ? null : result?.ToString();
    }
}
