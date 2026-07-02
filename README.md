# K2 Azure Migration Utility

A WPF desktop tool that prepares an on-premises K2 blackpearl database for migration to Azure SQL. It has two phases:

1. **Decrypt for Migration** — strips the SQL Server symmetric key encryption that Azure SQL cannot carry across (see below).
2. **Schema Sync (Azure DACPAC)** — deploys the schema from a given K2 version's own Azure-targeted DACPAC against the database, so what gets migrated matches exactly what that K2 version's installer expects — not just whatever schema state the source database happened to be in.

Phase 2 exists because a straight BACPAC copy only ever carries the schema the source database had at export time. If that source predates a K2 patch that added a column, procedure, or other object, the copy will too — and K2's setup does not reliably self-heal that gap during an upgrade (its own "skip files/objects that already look present" logic isn't version-aware). Running Phase 2 against the target K2 version's DACPAC closes that gap before you ever touch the setup wizard.

---

## Why this exists

K2 blackpearl uses completely different encryption strategies depending on whether the database backend is on-premises SQL Server or Azure SQL, and there is no supported migration path between the two.

### On-premises SQL Server

K2 creates a full SQL Server symmetric key stack inside the K2 database at install time:

```
Database Master Key  →  Certificate (SCHostServerCert)  →  Symmetric Key (SCSSOKey, AES-256)
```

Sensitive configuration values — connection strings, certificates, passwords, signing keys — are stored as **encrypted binary blobs** in `[HostServer].[Configuration]` using SQL Server's `EncryptByKey()` function. A flag row `[USESQLENCRYPTION] = True` tells K2 to open the symmetric key before reading any of these values.

### Azure SQL

K2 does **not** create the Master Key / Certificate / Symmetric Key stack on Azure SQL. Configuration values are stored as **plaintext**. The `[USESQLENCRYPTION]` flag is `False`. A separate Windows certificate thumbprint (`[AZURE_THUMBPRINT]`) handles Azure service authentication — it has nothing to do with database encryption.

### Why you can't just move the database

Moving a database to Azure SQL requires a **BACPAC export**. BACPAC exports schema DDL and row data — it does not preserve:

- The Database Master Key
- Certificate private keys (`CREATE CERTIFICATE` on the destination generates a fresh unrelated key pair)
- The AES key material stored inside the Symmetric Key

When the BACPAC is imported into Azure SQL, the encrypted binary blobs in `[HostServer].[Configuration]` become permanently unreadable — the keys that encrypted them no longer exist. K2's setup for Azure never attempts to open a symmetric key and has no migration path that bridges the two states.

Moving between **on-premises SQL Servers** works because `BACKUP/RESTORE` is a file-level copy that preserves all cryptographic objects intact.

> This analysis is based on decompilation of K2 blackpearl v5.0009.1000.0 / 5.8_R0_20240902.2 setup binaries (`SourceCode.SetupManager.exe`, `SourceCode.Install.Package.dll`, `SourceCode.Install.SQL.dll`). See `Y:\Temp\K2OnPremToAzure\K2-OnPrem-To-Azure-Migration-Analysis.md` for the full technical write-up.

### The solution

Run this utility against the source on-premises database **before** the BACPAC export, while the original cryptographic objects are still intact. It:

1. Opens the symmetric key using the existing certificate (which is preserved in the source database)
2. Decrypts every encrypted row back to plaintext in a single transaction
3. Sets `[USESQLENCRYPTION]` to `False`
4. Optionally drops the Master Key, Certificate, and Symmetric Key

After the utility completes, the database contains only plaintext values and has no dependency on any SQL Server cryptographic objects. It can then be exported as a BACPAC and imported into Azure SQL normally.

---

## Prerequisites

- The **source on-premises SQL Server** must still be accessible (the utility must run before the BACPAC export, not after)
- The SQL login used must be **`db_owner`** on the K2 database
- **K2 services should be stopped** before running — to prevent K2 from writing new encrypted values mid-migration
- A **database backup should exist** — the decryption step makes in-place, irreversible changes to the database

---

## Usage

1. Launch `K2AzureMigrator.exe`
2. Enter the **Server** and **Database** name (defaults: `.` and `K2`)
3. Choose **Windows Authentication** or supply SQL credentials
4. Enter the **Master Key Password** if it was changed from the K2 default (leave blank to use the default: `5CE05F96-98A1-475C-9E8C-5053F057D312`)
5. Click **Test Connection** to verify connectivity
6. Click **Run Checks** — the tool runs 10 pre-flight checks and then lists all encrypted tokens in the Discovery panel
7. Optionally click **Dry Run** to see exactly what would change without touching anything
8. When ready, click **Execute Migration** — confirm the dialog, and the tool decrypts the database
9. Click **Save Report** to export the full log as a `.txt` file

### Options

| Option | Default | Effect |
|---|---|---|
| Drop encryption objects after migration | On | Drops `SCSSOKey`, `SCHostServerCert`, and the Master Key after decryption. Recommended — prevents the BACPAC from including schema objects that are meaningless on Azure. |
| Dry run (no changes) | Off | Reports what would change without modifying any data. Safe to run on a live database. |

---

## Pre-flight Checks

The tool validates the following before allowing execution:

| Check | Type |
|---|---|
| SQL Connectivity | Required |
| K2 Schema Present (`[HostServer].[Configuration]` exists) | Required |
| SQL Encryption Active (`[USESQLENCRYPTION]` = True) | Required |
| Symmetric Key `SCSSOKey` exists | Required |
| Certificate `SCHostServerCert` exists | Required |
| Master Key openable with supplied password | Required |
| Symmetric Key openable via the certificate | Required |
| Decryption test (all encrypted rows return non-NULL) | Required |
| Recent database backup exists in msdb | Warning only |
| No active K2 sessions detected | Warning only |

If any **Required** check fails, the Execute and Dry Run buttons remain disabled.

The decryption test is particularly important: if the database was restored from another server and the encryption key was subsequently recreated, the encrypted binary values will return `NULL` from `DecryptByKey()` — meaning they were encrypted with a key that no longer matches. The tool detects and reports this before making any changes.

---

## What happens during Execute

All data changes run inside a single SQL transaction with `XACT_ABORT ON`. If anything fails mid-way, the entire transaction is rolled back and the database is left unchanged.

```
1. OPEN MASTER KEY DECRYPTION BY PASSWORD = @password
2. OPEN SYMMETRIC KEY [SCSSOKey] DECRYPTION BY CERTIFICATE [SCHostServerCert]
3. Verify all encrypted rows are decryptable (pre-check before any writes)
4. BEGIN TRANSACTION
5.   UPDATE [HostServer].[Configuration]
        SET [VariableValue] = CONVERT(NVARCHAR(MAX), DecryptByKey([VariableValue])),
            [Encrypted] = 0
        WHERE [Encrypted] = 1
          AND CONVERT(NVARCHAR(MAX), DecryptByKey([VariableValue])) IS NOT NULL
6.   Decrypt any encrypted SmartBox columns (discovered via [Smartbox].[SmartboxObject] metadata)
7.   UPDATE [HostServer].[Configuration]
        SET [VariableValue] = 'False'
        WHERE [VariableToken] = '[USESQLENCRYPTION]'
8. COMMIT
9. (if option set) DROP SYMMETRIC KEY / DROP CERTIFICATE / DROP MASTER KEY
```

---

## After the utility completes (Phase 1)

1. **Run Phase 2 (Schema Sync)** — see below — either now against this same source database, or later against the Azure SQL database after import. Doing it now catches problems before you round-trip through a BACPAC.
2. **Export BACPAC** from the source database:
   ```
   SqlPackage.exe /Action:Export /SourceConnectionString:"Server=...;Database=K2;..." /TargetFile:"K2_azure_ready.bacpac"
   ```
3. **Import BACPAC** into Azure SQL via the Azure Portal, SSMS, or SqlPackage
4. If Phase 2 wasn't run pre-export, **run it now** against the Azure SQL database — this is the step that actually matters, since it's the database K2 setup will connect to
5. **Run K2 setup** pointing at the new Azure SQL instance — the setup will detect Azure (via `SELECT @@VERSION` regex), confirm `[USESQLENCRYPTION]` = False, and configure the environment without attempting to create or open any symmetric keys

---

## Schema Sync (Azure DACPAC)

### Why this exists

A BACPAC only ever contains the schema the source database had at the moment of export. If the on-prem K2 instance you're migrating from is behind the target K2 version — even by one cumulative patch — any column, table, or stored procedure added since won't be in the copy. K2's setup does not reliably fill that gap on its own: its file/object "skip if it looks already present" logic checks for existence, not version, so a stale schema can sit there silently until something at runtime actually needs the missing piece (observed directly during a 5.8→5.9 upgrade in this environment: a post-upgrade data-fixup script failed with `Invalid column name 'TemplateValue'` because the migrated database predated that column).

Every K2 installer ships the schema it expects as a DACPAC — a declarative, versioned schema snapshot used by Microsoft's `SqlPackage`/DacFx tooling — split into two platform variants:

```
Installation\Data\SourceCode.Data.All.SqlServer.zip\SourceCode.Data.All.dacpac   (on-prem SQL Server target)
Installation\Data\SourceCode.Data.All.AzureDb.zip\SourceCode.Data.All.dacpac     (Azure SQL target)
```

This phase points the **Azure** variant at your working database and lets DacFx compute and apply whatever schema diff is needed to bring it fully in line with that K2 version's baseline — in one pass, rather than discovering gaps one crash at a time during setup.

### Usage

1. In the **Server / Database / Auth** fields at the top (shared with Phase 1), connect to whichever database you want to sync — the on-prem source pre-export, or the Azure SQL database post-import
2. Switch to the **2. Schema Sync (Azure DACPAC)** tab
3. In **DACPAC Source**, browse to (or paste the path of) the target K2 version's installation media root, the `SourceCode.Data.All.AzureDb.zip` file directly, or an already-extracted `.dacpac`
4. Click **Locate** — confirms the package was found and loaded, and warns if the filename suggests you picked the on-prem `SqlServer.zip` variant by mistake
5. Click **Generate Script** to preview the exact T-SQL that would run, with no changes made — safe on a live database. The full script is written to the log
6. Review the script, then click **Apply Schema Sync** to actually deploy it — confirm the dialog to proceed
7. **Save Report** captures both phases' logs together

### Options

| Option | Default | Effect |
|---|---|---|
| Block deployment if a change could lose data | On | Maps to DacFx's `BlockOnPossibleDataLoss`. Recommended — forces you to look at anything that would truncate or drop data-bearing objects rather than silently proceeding. |
| Drop objects not present in the DACPAC | Off | Maps to DacFx's `DropObjectsNotInSource`. **Leave this off** unless you specifically want an exact byte-for-byte schema match — a live K2 database can legitimately have objects (SmartObject-generated tables, customizations) that aren't part of the base DACPAC model, and this option would drop them. |

### Implementation notes

- Uses the `Microsoft.SqlServer.DacFx` NuGet package directly (`Microsoft.SqlServer.Dac.DacServices`) — no dependency on `SqlPackage.exe` being installed separately
- `DacServices.Script(...)` generates the diff without touching the database (Generate Script); `DacServices.Deploy(...)` applies it (Apply Schema Sync) — same dry-run/execute pattern as Phase 1
- The located `.dacpac` is extracted to a temp file per run; nothing is written back into the install media

---

## Project structure

```
K2AzureMigrator/
├── K2AzureMigrator.csproj      .NET 8 WPF project
├── App.xaml / App.xaml.cs
├── MainWindow.xaml              UI layout — two tabs sharing one connection panel and log
├── MainWindow.xaml.cs           Event handlers and UI logic
└── Services/
    ├── MigrationService.cs      Phase 1 — encryption pre-flight, discovery, decrypt/execute
    ├── SchemaSyncService.cs     Phase 2 — locate DACPAC, generate script, deploy via DacFx
    └── Logger.cs                Shared file-based error logging
```

**Dependencies:** `Microsoft.Data.SqlClient 5.2.2`, `Microsoft.SqlServer.DacFx 162.3.566`

---

## Limitations

- Targets K2 blackpearl specifically — the schema assumptions (`[HostServer].[Configuration]`, `[Smartbox].[SmartboxObject]`, etc.) are K2-specific
- Phase 1 requires direct SQL Server access to the source database — cannot work against an already-imported Azure SQL BACPAC
- Does not perform the BACPAC export or Azure SQL import — those steps are intentionally left to standard tooling
- If `DecryptByKey()` returns NULL for any rows (wrong key, or data encrypted on a different server instance), those rows are skipped and flagged in the report rather than written as NULL
- Phase 2's "is this the Azure variant" check is filename-based (it looks for `AzureDb` in the zip name) — it does not deeply inspect the DACPAC's internal schema-provider metadata, so a renamed zip could slip past the warning
- Phase 2 fixes schema drift only. It does not address stale-but-present binaries or GAC assemblies on the K2 application server itself — that is a separate class of problem (see the migration analysis doc) and is unrelated to the database
