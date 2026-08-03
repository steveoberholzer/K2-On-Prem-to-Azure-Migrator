using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

            string tempDir = Path.Combine(Path.GetTempPath(), $"K2AzureMigrator_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string tempDacpac = Path.Combine(tempDir, DacpacEntryName);

            bool masterFromZip = false;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry(DacpacEntryName);
                if (entry == null)
                {
                    result.Message = $"'{zipName}' does not contain '{DacpacEntryName}' — is this the right zip?";
                    return result;
                }
                entry.ExtractToFile(tempDacpac, overwrite: true);

                // K2's AzureDb zip ships its own master.dacpac alongside the DACPAC — prefer
                // it since it's exactly what K2 tested against.
                var masterEntry = archive.GetEntry("master.dacpac");
                if (masterEntry != null)
                {
                    masterEntry.ExtractToFile(Path.Combine(tempDir, "master.dacpac"), overwrite: true);
                    masterFromZip = true;
                }
            }

            // The K2 AzureDb DACPAC is compiled with SqlAzureDatabaseSchemaProvider.  When
            // DacFx deploys it against an on-prem SQL Server, it tries to model the target
            // database's filegroup objects (e.g. [PRIMARY]) using the Azure DSP's built-in
            // type table — which doesn't contain filegroups — and fails with SQL0.
            // Patching the DSP to Sql150DatabaseSchemaProvider makes DacFx use the SQL Server
            // built-in types (which do include [PRIMARY]) while keeping the schema content
            // identical.
            PatchDacpacDsp(tempDacpac);

            // DacFx resolves external references (master.dacpac) by searching the directory
            // that contains the DACPAC being loaded.  Fall back to the embedded SQL Server 150
            // variant if the zip didn't bundle one (so the tool works without SSDT installed).
            bool masterResolved = masterFromZip || TryExtractMasterDacpac(Path.Combine(tempDir, "master.dacpac"));

            var loaded = LoadDacpacMetadata(tempDacpac, zipName, result);
            string masterSource = masterFromZip ? "bundled in zip" : "embedded SQL Server 150 variant";
            loaded.Message += masterResolved
                ? $"\n  master.dacpac resolved ({masterSource})."
                : "\n  WARNING: master.dacpac could not be extracted — DacFx may report external-reference errors.";
            return loaded;
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

    /// <summary>
    /// Rewrites the DspName in the DACPAC's model.xml from SqlAzureDatabaseSchemaProvider to
    /// Sql150DatabaseSchemaProvider so DacFx uses SQL Server built-in types (including the
    /// [PRIMARY] filegroup) when generating the deployment plan against an on-prem target.
    /// Also updates the SHA-256 checksum in Origin.xml so DacFx's integrity check passes.
    /// </summary>
    private static void PatchDacpacDsp(string dacpacPath)
    {
        const string azureDsp = "Microsoft.Data.Tools.Schema.Sql.SqlAzureDatabaseSchemaProvider";
        const string sql150Dsp = "Microsoft.Data.Tools.Schema.Sql.Sql150DatabaseSchemaProvider";

        using var zip = ZipFile.Open(dacpacPath, ZipArchiveMode.Update);

        // --- patch model.xml ---
        var modelEntry = zip.GetEntry("model.xml");
        if (modelEntry == null) return;

        byte[] originalBytes;
        using (var ms = new MemoryStream())
        {
            using (var s = modelEntry.Open()) s.CopyTo(ms);
            originalBytes = ms.ToArray();
        }

        // Replace DSP in the raw bytes to preserve the original encoding exactly.
        // The BOM (if any) and byte order stay untouched so the written bytes are
        // identical except for the DSP string — ensuring the checksum we compute
        // matches what DacFx will verify.
        var oldDspBytes = Encoding.UTF8.GetBytes(azureDsp);
        var newDspBytes = Encoding.UTF8.GetBytes(sql150Dsp);
        var originalStr  = Encoding.UTF8.GetString(originalBytes);
        if (!originalStr.Contains(azureDsp)) return;

        var patchedStr   = originalStr.Replace(azureDsp, sql150Dsp);
        var patchedBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: originalBytes is [0xEF, 0xBB, 0xBF, ..])
                               .GetBytes(patchedStr);

        modelEntry.Delete();
        var newModelEntry = zip.CreateEntry("model.xml");
        using (var s = newModelEntry.Open()) s.Write(patchedBytes);

        // --- update checksum in Origin.xml ---
        var originEntry = zip.GetEntry("Origin.xml");
        if (originEntry != null)
        {
            string originXml;
            using (var s = originEntry.Open())
            using (var r = new StreamReader(s, Encoding.UTF8))
                originXml = r.ReadToEnd();

            var newChecksum = Convert.ToHexString(SHA256.HashData(patchedBytes));
            // Replace the /model.xml checksum value (hex, 64 chars) in the XML.
            originXml = System.Text.RegularExpressions.Regex.Replace(
                originXml,
                @"(?<=<Checksum Uri=""/model\.xml"">)[0-9A-Fa-f]{64}(?=</Checksum>)",
                newChecksum);

            originEntry.Delete();
            var newOriginEntry = zip.CreateEntry("Origin.xml");
            var originBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(originXml);
            using var s2 = newOriginEntry.Open();
            s2.Write(originBytes);
        }
    }

    /// <summary>
    /// Extracts the embedded master.dacpac (SQL Server 150 variant, shipped with the tool) to
    /// <paramref name="destPath"/> so DacFx can resolve external references in the K2 DACPAC.
    /// </summary>
    private static bool TryExtractMasterDacpac(string destPath)
    {
        const string resourceName = "K2AzureMigrator.Resources.master.dacpac";
        using var stream = typeof(SchemaSyncService).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return false;
        using var file = File.Create(destPath);
        stream.CopyTo(file);
        return true;
    }

    private static DacDeployOptions BuildDeployOptions(SchemaSyncOptions options) => new()
    {
        DropObjectsNotInSource = options.DropObjectsNotInSource,
        BlockOnPossibleDataLoss = options.BlockOnPossibleDataLoss,
        GenerateSmartDefaults = true,
        AllowIncompatiblePlatform = true,
        IncludeCompositeObjects = true,
        IgnoreFilegroupPlacement = true,
        TreatVerificationErrorsAsWarnings = true,
        CommandTimeout = 300,
    };
}
