using System.Windows;
using RaceNetScraper.App.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace RaceNetScraper.App.Components;

public partial class BucketView : System.Windows.Controls.UserControl
{
    private readonly BucketViewModel _viewModel = new();

    public BucketView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Re-reads settings.json and re-lists the bucket — called on first load and again
    /// every time MainWindow's TabControl selects this tab (see MainTabs_SelectionChanged), since
    /// the Scraper tab's bucket-name field can change what's configured while this tab isn't the
    /// one showing, and TabItem content isn't torn down/recreated on every switch (Loaded alone
    /// wouldn't fire again to catch that).</summary>
    public Task RefreshAsync() => _viewModel.RefreshCommand.ExecuteAsync(null);

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Choose file(s) to upload to the bucket",
            Multiselect = true,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (dialog.FileNames.Length == 0) return;

        await _viewModel.UploadAsync(dialog.FileNames);
    }

    private async void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not S3ObjectRow row) return;

        var confirm = MessageBox.Show(
            $"Delete '{row.Key}' from the bucket? This cannot be undone.",
            "Delete file", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await _viewModel.DeleteAsync(new[] { row.Key });
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var keys = _viewModel.Objects.Where(o => o.IsSelected).Select(o => o.Key).ToList();
        if (keys.Count == 0) return;

        var confirm = MessageBox.Show(
            $"Delete {keys.Count} file(s) from the bucket? This cannot be undone.",
            "Delete files", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await _viewModel.DeleteAsync(keys);
    }
}
