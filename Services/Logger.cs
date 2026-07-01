using System.IO;

namespace K2AzureMigrator.Services;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "logs");

    public static void LogError(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string logFile = Path.Combine(LogDir, $"K2AzureMigrator_{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(logFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
