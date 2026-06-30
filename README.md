# K2 Azure Migration Utility

A WPF desktop tool that prepares an on-premises K2 blackpearl database for migration to Azure SQL by stripping the SQL Server symmetric key encryption that Azure SQL cannot carry across.

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

## After the utility completes

1. **Export BACPAC** from the source database:
   ```
   SqlPackage.exe /Action:Export /SourceConnectionString:"Server=...;Database=K2;..." /TargetFile:"K2_azure_ready.bacpac"
   ```
2. **Import BACPAC** into Azure SQL via the Azure Portal, SSMS, or SqlPackage
3. **Run K2 setup** pointing at the new Azure SQL instance — the setup will detect Azure (via `SELECT @@VERSION` regex), confirm `[USESQLENCRYPTION]` = False, and configure the environment without attempting to create or open any symmetric keys

---

## Project structure

```
K2AzureMigrator/
├── K2AzureMigrator.csproj      .NET 9 WPF project
├── App.xaml / App.xaml.cs
├── MainWindow.xaml              UI layout
├── MainWindow.xaml.cs           Event handlers and UI logic
└── Services/
    └── MigrationService.cs      All SQL logic — pre-flight, discovery, execute
```

**Dependencies:** `Microsoft.Data.SqlClient 5.2.2` (no other external packages)

---

## Limitations

- Targets K2 blackpearl specifically — the schema assumptions (`[HostServer].[Configuration]`, `[Smartbox].[SmartboxObject]`, etc.) are K2-specific
- Requires direct SQL Server access to the source database — cannot work against an already-imported Azure SQL BACPAC
- Does not perform the BACPAC export or Azure SQL import — those steps are intentionally left to standard tooling
- If `DecryptByKey()` returns NULL for any rows (wrong key, or data encrypted on a different server instance), those rows are skipped and flagged in the report rather than written as NULL
