using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RaceNetScraper.App.ViewModels;

namespace RaceNetScraper.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
    }

    // Re-reads the Bucket tab's settings every time it's selected — the Scraper tab's own bucket
    // name field can change what's configured while the Bucket tab isn't showing, and its content
    // isn't recreated on every switch, so nothing would otherwise notice that change.
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem { Header: "Bucket" })
        {
            await BucketViewControl.RefreshAsync();
        }
    }

    // Makes the native Windows title bar/chrome dark too, so it matches the dark theme
    // used throughout the rest of the window (Windows 10 1809+ / Windows 11).
    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useImmersiveDarkMode = 1;
            var result = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));
            if (result != 0)
            {
                // Older Windows 10 builds used attribute 19 instead of 20.
                DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
            }
        }
        catch
        {
            // Best-effort cosmetic touch only — never let this affect app startup.
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
}
