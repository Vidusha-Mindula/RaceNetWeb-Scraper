using System.Configuration;
using System.Data;
using System.Windows;
using Microsoft.Win32;

namespace RaceNetScraper.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RaceNetScraper";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EnsureStartupRegistered();
    }

    /// <summary>Registers this exe to launch at Windows logon via the per-user Run key, so
    /// auto-scrape (see MainViewModel's DispatcherTimer, opt-in via AutoScrapeEnabled) actually
    /// fires at its configured times without anyone having to remember to open the app first. Runs
    /// on every startup rather than once at install time, so it self-heals if the entry is ever
    /// removed and stays pointed at the right path after an in-place update moves the exe. Per-user
    /// (HKEY_CURRENT_USER), so it needs no admin rights and only starts the app for the user who
    /// launched it at least once. Best-effort: a locked-down machine without registry write access
    /// shouldn't stop the app from running normally.</summary>
    private static void EnsureStartupRegistered()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(RunValueName, $"\"{exePath}\"");
        }
        catch
        {
            // Startup registration is a convenience, not a requirement — never block the app over it.
        }
    }
}
