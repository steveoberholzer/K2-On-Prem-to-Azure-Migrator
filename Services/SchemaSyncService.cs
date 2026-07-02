using System.IO;
using System.IO.Compression;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace K2AzureMigrator.Services;

public class DacpacLocateResult
{
    public bool Found { get; set; }
    public string? DacpacPath { get; set; }
    public string? SourceZipName { get; set; }
    public string? Version { get; set; }
    public string? Message { get; set; }
    public bool LooksLikeAzureVariant { get; set; }
}

public class SchemaSyncOptions
{
    public bool DropObjectsNotInSource { get; set; } = false;
    public bool BlockOnPossibleDataLoss { get; set; } = true;
}

public class SchemaSyncResult
{
    public bool Success { get; set; }
    public int ChangeCount { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Deploys the schema from a K2 installer's Azure DACPAC (SourceCode.Data.All.AzureDb.zip
/// -&gt; SourceCode.Data.All.dacpac) against a working database, so a migrated database ends up
/// with exactly the schema the target K2 version expects — closing the gap left by a straight
/// BACPAC copy, which only carries whatever schema the source happened to have at export time.
/// </summary>
public class SchemaSyncService
{
    private const string AzureDacpacZipName = "SourceCode.Data.All.AzureDb.zip";
    private const string DacpacEntryName = "SourceCode.Data.All.dacpac";

    /// <summary>
    /// Accepts a path to either: a .dacpac file directly, the AzureDb .zip directly, or a
    /// K2 installation root/media folder to search under.
    /// </summary>
    public DacpacLocateResult LocateDacpac(string inputPath)
    {
        var result = new DacpacLocateResult();

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            result.Message = "No path supplied.";
            return result;
        }

        try
        {
            string? zipPath = null;

            if (File.Exists(inputPath) && inputPath.EndsWith(".dacpac", StringComparison.OrdinalIgnoreCase))
            {
                return LoadDacpacMetadata(inputPath, sourceZipName: null, result);
            }

            if (File.Exists(inputPath) && inputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipPath = inputPath;
            }
            else if (Directory.Exists(inputPath))
            {
                zipPath = Directory.EnumerateFiles(inputPath, AzureDacpacZipName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (zipPath == null)
                {
                    result.Message = $"Could not find '{AzureDacpacZipName}' anywhere under '{inputPath}'. " +
                                      "Point this at the K2 installation media root, or directly at the zip/dacpac file.";
                    return result;
                }
            }
            else
            {
                result.Message = $"Path not found: {inputPath}";
                return result;
            }

            string zipName = Path.GetFileName(zipPath);
            string tempDacpac = Path.Combine(Path.GetTempPath(), $"K2AzureMigrator_{Guid.NewGuid():N}.dacpac");

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry(DacpacEntryName);
                if (entry == null)
                {
                    result.Message = $"'{zipName}' does not contain '{DacpacEntryName}' — is this the right zip?";
                    return result;
                }
                entry.ExtractToFile(tempDacpac, overwrite: true);
            }

            return LoadDacpacMetadata(tempDacpac, zipName, result);
        }
        catch (Exception ex)
        {
            Logger.LogError("LocateDacpac", ex);
            result.Message = $"Error locating DACPAC: {ex.Message}";
            return result;
        }
    }

    private static DacpacLocateResult LoadDacpacMetadata(string dacpacPath, string? sourceZipName, DacpacLocateResult result)
    {
        using var package = DacPackage.Load(dacpacPath);
        result.Found = true;
        result.DacpacPath = dacpacPath;
        result.SourceZipName = sourceZipName;
        result.Version = package.Version?.ToString();

        // Best-effort guard: this tool is only meaningful against the Azure-targeted dacpac.
        // The on-prem SqlServer.zip variant ships under the same package Name, so the filename
        // (when we found it via search/zip) is the most reliable signal available without
        // pulling in the heavier TSqlModel APIs just to read DspName.
        result.LooksLikeAzureVariant = sourceZipName == null
            || sourceZipName.Contains("AzureDb", StringComparison.OrdinalIgnoreCase);

        result.Message = sourceZipName != null
            ? $"Loaded '{DacpacEntryName}' from '{sourceZipName}' — {package.Name} v{package.Version}"
            : $"Loaded '{Path.GetFileName(dacpacPath)}' — {package.Name} v{package.Version}";

        if (!result.LooksLikeAzureVariant)
            result.Message += "  ⚠ WARNING: filename doesn't look like the Azure variant — check you didn't point at SourceCode.Data.All.SqlServer.zip instead.";

        return result;
    }

    /// <summary>
    /// Generates the deployment (upgrade) T-SQL script without applying it — safe to run
    /// against a live database, mirroring the existing Dry Run pattern for the decrypt phase.
    /// </summary>
    public Task<string> GenerateDeployScriptAsync(
        string connectionString,
        string dacpacPath,
        SchemaSyncOptions options,
        IProgress<string> log,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            void Log(string msg) => log.Report($"[{DateTime.Now:HH:mm:ss}] {msg}");

            using var package = DacPackage.Load(dacpacPath);
            var dacServices = new DacServices(connectionString);
            dacServices.Message += (_, e) => Log($"  [DacFx] {e.Message}");

            string dbName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            var publishOptions = new PublishOptions { DeployOptions = BuildDeployOptions(options) };

            Log($"Generating deployment script for database '{dbName}'...");
            ct.ThrowIfCancellationRequested();
            string script = dacServices.Script(package, dbName, publishOptions).DatabaseScript;
            Log(string.IsNullOrWhiteSpace(script)
                ? "No changes required — database schema already matches the DACPAC."
                : $"Script generated ({script.Split('\n').Length} lines). Review before applying.");

            return script;
        }, ct);
    }

    /// <summary>
    /// Applies the DACPAC's schema to the target database (additive-by-default; see
    /// SchemaSyncOptions for the two destructive-change toggles).
    /// </summary>
    public Task<SchemaSyncResult> DeploySchemaAsync(
        string connectionString,
        string dacpacPath,
        SchemaSyncOptions options,
        IProgress<string> log,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            void Log(string msg) => log.Report($"[{DateTime.Now:HH:mm:ss}] {msg}");
            var result = new SchemaSyncResult();

            try
            {
                using var package = DacPackage.Load(dacpacPath);
                var dacServices = new DacServices(connectionString);
                dacServices.Message += (_, e) => Log($"  [DacFx] {e.Message}");
                dacServices.ProgressChanged += (_, e) => Log($"  [DacFx] {e.Status}: {e.Message}");

                string dbName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
                var deployOptions = BuildDeployOptions(options);

                Log($"Deploying schema to '{dbName}' (upgrading existing database)...");
                dacServices.Deploy(package, dbName, upgradeExisting: true, deployOptions, ct);

                Log("Schema deployment complete.");
                result.Success = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError("DeploySchemaAsync", ex);
                Log($"ERROR: {ex.Message}");
                result.ErrorMessage = ex.Message;
            }

            return result;
        }, ct);
    }

    private static DacDeployOptions BuildDeployOptions(SchemaSyncOptions options) => new()
    {
        DropObjectsNotInSource = options.DropObjectsNotInSource,
        BlockOnPossibleDataLoss = options.BlockOnPossibleDataLoss,
        GenerateSmartDefaults = true,
        AllowIncompatiblePlatform = true,
        IncludeCompositeObjects = true,
        CommandTimeout = 300,
    };
}
