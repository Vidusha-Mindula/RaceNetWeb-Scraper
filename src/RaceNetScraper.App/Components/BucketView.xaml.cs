using System.Windows;
using RaceNetScraper.App.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace RaceNetScraper.App.Components;

public partial class BucketView : System.Windows.Controls.UserControl
{
    private readonly BucketViewModel _viewModel = new();
    private bool _loaded;

    public BucketView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // TabItem content stays alive after the tab is switched away from, so Loaded can fire
        // more than once (e.g. re-docking) — only auto-refresh the first time.
        if (_loaded) return;
        _loaded = true;
        await _viewModel.RefreshCommand.ExecuteAsync(null);
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
