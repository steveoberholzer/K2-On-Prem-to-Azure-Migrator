using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using K2AzureMigrator.Services;
using Microsoft.Win32;

namespace K2AzureMigrator;

public partial class MainWindow : Window
{
    private readonly MigrationService _svc = new();
    private readonly ObservableCollection<PreFlightCheck> _checks = [];
    private CancellationTokenSource? _cts;
    private string _lastLog = "";
    private bool _checksPassedForExecute;

    public MainWindow()
    {
        InitializeComponent();
        CheckList.ItemsSource = _checks;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private string ConnectionString()
    {
        bool trusted = ChkTrusted.IsChecked == true;
        return MigrationService.BuildConnectionString(
            TxtServer.Text.Trim(),
            TxtDatabase.Text.Trim(),
            trusted,
            TxtUsername.Text.Trim(),
            trusted ? "" : PwdSqlPassword.Password);
    }

    private string MasterKeyPassword()
    {
        string pwd = PwdMasterKey.Password;
        return string.IsNullOrWhiteSpace(pwd) ? MigrationService.DefaultMasterKeyPassword : pwd;
    }

    private void AppendLog(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            _lastLog += msg + "\n";
            TxtLog.Text = _lastLog;
            LogScroller.ScrollToBottom();
        });
    }

    private void SetStatus(string text)
        => Dispatcher.Invoke(() => TxtHeaderStatus.Text = text);

    private void SetBusy(bool busy)
    {
        Dispatcher.Invoke(() =>
        {
            BtnTestConn.IsEnabled = !busy;
            BtnRunChecks.IsEnabled = !busy;
            BtnDryRun.IsEnabled = !busy && _checksPassedForExecute;
            BtnExecute.IsEnabled = !busy && _checksPassedForExecute;
            BtnSaveReport.IsEnabled = !busy && _lastLog.Length > 0;
        });
    }

    // ── Connection ───────────────────────────────────────────────────────────

    private void ChkTrusted_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelSqlAuth == null) return;
        PanelSqlAuth.Visibility = ChkTrusted.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BtnTestConn_Click(object sender, RoutedEventArgs e)
    {
        TxtConnResult.Text = "Testing…";
        TxtConnResult.Foreground = (SolidColorBrush)FindResource("LabelBrush");
        BtnTestConn.IsEnabled = false;
        BtnRunChecks.IsEnabled = false;

        var (ok, msg) = await _svc.TestConnectionAsync(ConnectionString());

        TxtConnResult.Text = ok ? $"✓ {msg}" : $"✗ {msg}";
        TxtConnResult.Foreground = ok
            ? (SolidColorBrush)FindResource("PassBrush")
            : (SolidColorBrush)FindResource("FailBrush");

        BtnTestConn.IsEnabled = true;
        BtnRunChecks.IsEnabled = ok;
    }

    // ── Pre-flight Checks ────────────────────────────────────────────────────

    private async void BtnRunChecks_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        _checks.Clear();
        TxtConfigCount.Text = "—";
        TxtSmartboxCount.Text = "—";
        TokenList.ItemsSource = null;
        SmartboxList.ItemsSource = null;
        _checksPassedForExecute = false;
        SetBusy(true);
        SetStatus("Running pre-flight checks…");
        AppendLog("─── Pre-flight checks ───────────────────────────────");

        try
        {
            var tempChecks = new List<PreFlightCheck>();
            await _svc.RunPreFlightChecksAsync(
                ConnectionString(),
                MasterKeyPassword(),
                tempChecks,
                check =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        // First time a check arrives — add to collection
                        if (!_checks.Any(c => c.Name == check.Name))
                            _checks.Add(check);
                        // Force collection refresh for status change
                        int idx = _checks.IndexOf(check);
                        if (idx >= 0)
                        {
                            _checks.RemoveAt(idx);
                            _checks.Insert(idx, check);
                        }
                        string icon = check.Status switch
                        {
                            CheckStatus.Pass    => "✓",
                            CheckStatus.Fail    => "✗",
                            CheckStatus.Warning => "⚠",
                            CheckStatus.Running => "…",
                            _                  => "○"
                        };
                        string detail = string.IsNullOrEmpty(check.Detail) ? "" : $"  {check.Detail}";
                        if (check.Status is CheckStatus.Pass or CheckStatus.Fail or CheckStatus.Warning)
                            AppendLog($"  {icon} {check.Name}{detail}");
                    });
                },
                _cts.Token);

            // Evaluate overall result
            bool anyFail = _checks.Any(c => c.Status == CheckStatus.Fail);
            _checksPassedForExecute = !anyFail;

            string summary = anyFail ? "Pre-flight FAILED — fix errors before executing" : "Pre-flight passed";
            AppendLog($"\n  → {summary}");
            SetStatus(anyFail ? "Pre-flight failed" : "Pre-flight passed");

            // Run discovery if pre-flight didn't hard-fail connectivity
            bool canDiscover = _checks.FirstOrDefault(c => c.Name == "SQL Connectivity")?.Status == CheckStatus.Pass
                            && _checks.FirstOrDefault(c => c.Name == "K2 Schema Present")?.Status == CheckStatus.Pass;

            if (canDiscover)
            {
                AppendLog("\n─── Discovery ───────────────────────────────────────");
                try
                {
                    var discovery = await _svc.DiscoverAsync(ConnectionString(), _cts.Token);
                    TxtConfigCount.Text = discovery.ConfigEncryptedCount.ToString();
                    TxtSmartboxCount.Text = discovery.SmartboxColumns.Count.ToString();
                    TokenList.ItemsSource = discovery.ConfigTokens;
                    SmartboxList.ItemsSource = discovery.SmartboxColumns.Count > 0
                        ? discovery.SmartboxColumns : null;

                    AppendLog($"  Encrypted config rows : {discovery.ConfigEncryptedCount}");
                    AppendLog($"  SmartBox enc. columns : {discovery.SmartboxColumns.Count}");
                    if (discovery.SmartboxColumns.Count > 0)
                        foreach (var col in discovery.SmartboxColumns)
                            AppendLog($"    {col.TableName}.{col.ColumnName} ({col.RowCount} rows)");
                    if (discovery.RecentBackupFound)
                        AppendLog($"  Last backup           : {discovery.BackupAge}");
                    if (discovery.ActiveK2Sessions > 0)
                        AppendLog($"  ⚠ Active K2 sessions  : {discovery.ActiveK2Sessions}");
                }
                catch (Exception ex)
                {
                    AppendLog($"  Discovery error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("  Cancelled.");
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"  ERROR: {ex.Message}");
            SetStatus("Error");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── Dry Run ──────────────────────────────────────────────────────────────

    private async void BtnDryRun_Click(object sender, RoutedEventArgs e)
        => await RunMigrationAsync(dryRun: true);

    // ── Execute ──────────────────────────────────────────────────────────────

    private async void BtnExecute_Click(object sender, RoutedEventArgs e)
    {
        string db = $"{TxtServer.Text.Trim()} / {TxtDatabase.Text.Trim()}";
        string dropMsg = ChkDropCrypto.IsChecked == true
            ? "\n\nEncryption objects (SCSSOKey, SCHostServerCert, Master Key) will also be DROPPED."
            : "";

        var confirm = MessageBox.Show(
            $"This will decrypt all encrypted configuration values in:\n\n  {db}\n\n" +
            $"Changes are permanent and cannot be automatically undone.{dropMsg}\n\n" +
            "Ensure a database backup exists before continuing.\n\nProceed?",
            "Confirm Migration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        await RunMigrationAsync(dryRun: false);
    }

    private async Task RunMigrationAsync(bool dryRun)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        string label = dryRun ? "Dry run" : "Migration";
        SetStatus($"{label} in progress…");
        AppendLog($"\n─── {label.ToUpper()} ──────────────────────────────────────────");

        bool dropCrypto = ChkDropCrypto.IsChecked == true;
        var progress = new Progress<string>(AppendLog);

        try
        {
            var result = await _svc.ExecuteAsync(
                ConnectionString(),
                MasterKeyPassword(),
                dryRun,
                dropCrypto,
                progress,
                _cts.Token);

            AppendLog("\n─── RESULT ──────────────────────────────────────────");
            if (result.Success)
            {
                AppendLog($"  Status              : {(dryRun ? "DRY RUN COMPLETE" : "SUCCESS")}");
                AppendLog($"  Config rows touched : {result.ConfigRowsDecrypted}");
                if (result.SmartboxColumnsDecrypted.Count > 0)
                    foreach (var c in result.SmartboxColumnsDecrypted)
                        AppendLog($"  SmartBox decrypted  : {c}");
                if (!dryRun)
                {
                    AppendLog($"  USESQLENCRYPTION    : {(result.UseSqlEncryptionCleared ? "Set to False ✓" : "NOT updated")}");
                    AppendLog($"  Crypto objects      : {(result.CryptoObjectsDropped ? "Dropped ✓" : dropCrypto ? "Drop attempted (see log)" : "Not dropped (option off)")}");
                    AppendLog("\n  Database is ready for BACPAC export.");
                    AppendLog("  Next: SqlPackage.exe /Action:Export /SourceConnectionString:\"...\" /TargetFile:\"K2_azure_ready.bacpac\"");
                }
                foreach (var w in result.Warnings)
                    AppendLog($"  ⚠ {w}");
                SetStatus(dryRun ? "Dry run complete" : "Migration complete ✓");
            }
            else
            {
                AppendLog($"  Status : FAILED");
                AppendLog($"  Error  : {result.ErrorMessage}");
                SetStatus($"{label} failed");
            }

            BtnSaveReport.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("  Cancelled.");
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"  FATAL: {ex.Message}");
            SetStatus("Error");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── Save Report ──────────────────────────────────────────────────────────

    private void BtnSaveReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Migration Report",
            FileName = $"K2AzureMigration_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "txt"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("K2 Azure Migration Utility — Report");
            sb.AppendLine(new string('=', 50));
            sb.AppendLine($"Generated : {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine($"Server    : {TxtServer.Text.Trim()}");
            sb.AppendLine($"Database  : {TxtDatabase.Text.Trim()}");
            sb.AppendLine($"Auth      : {(ChkTrusted.IsChecked == true ? "Windows Authentication" : $"SQL Auth ({TxtUsername.Text.Trim()})")}");
            sb.AppendLine();
            sb.AppendLine(_lastLog);
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"Report saved to:\n{dlg.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save report:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

// ── Value converter ──────────────────────────────────────────────────────────

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
