namespace NativeVox.App.Views;

using System.Windows;
using System.Windows.Controls;
using NativeVox.Core.Models;
using NativeVox.Core.Services;
using NativeVox.Infrastructure.Storage;

public partial class SetupWindow : Window
{
    private readonly IModelManager _modelManager;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _downloadCts;

    public SetupWindow(IModelManager modelManager, AppSettings settings)
    {
        InitializeComponent();
        _modelManager = modelManager;
        _settings = settings;

        PopulateModels();
        HotkeyDisplayText.Text = _settings.Hotkey.ToString();
    }

    private void PopulateModels()
    {
        var models = _modelManager.GetModels();
        ModelSelector.ItemsSource = models;
        ModelSelector.DisplayMemberPath = nameof(ModelInfo.DisplayName);

        var selected = models.FirstOrDefault(m => m.Size == _settings.SelectedModel) ?? models.First();
        ModelSelector.SelectedItem = selected;

        UpdateModelStatus(selected);
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelSelector.SelectedItem is ModelInfo selected)
        {
            _settings.SelectedModel = selected.Size;
            UpdateModelStatus(selected);
        }
    }

    private void UpdateModelStatus(ModelInfo model)
    {
        bool isDownloaded = _modelManager.IsDownloaded(model.Size);
        if (isDownloaded)
        {
            DownloadStatusText.Text = "Model already downloaded and ready.";
            DownloadPercentText.Text = "100%";
            DownloadProgressBar.Value = 100;
            DownloadButton.IsEnabled = false;
            DownloadButton.Content = "Downloaded";
            CompleteButton.IsEnabled = true;
        }
        else
        {
            DownloadStatusText.Text = $"Ready to download ({model.SizeInMegabytes} MB)";
            DownloadPercentText.Text = "0%";
            DownloadProgressBar.Value = 0;
            DownloadButton.IsEnabled = true;
            DownloadButton.Content = "Download Model";
            CompleteButton.IsEnabled = false;
        }
    }

    private async void OnDownloadClicked(object sender, RoutedEventArgs e)
    {
        if (ModelSelector.SelectedItem is not ModelInfo selected) return;

        DownloadButton.IsEnabled = false;
        ModelSelector.IsEnabled = false;
        DownloadStatusText.Text = "Downloading...";
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<double>(value =>
        {
            DownloadProgressBar.Value = value;
            DownloadPercentText.Text = $"{value:0}%";
            DownloadStatusText.Text = $"Downloading {selected.DisplayName}... ({value:0}%)";
        });

        try
        {
            await _modelManager.DownloadModelAsync(selected.Size, progress, _downloadCts.Token);
            DownloadStatusText.Text = "Download complete!";
            DownloadButton.Content = "Downloaded";
            CompleteButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "Download cancelled.";
            DownloadButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = $"Error: {ex.Message}";
            DownloadButton.IsEnabled = true;
            MessageBox.Show($"Failed to download model: {ex.Message}", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ModelSelector.IsEnabled = true;
        }
    }

    private void OnCompleteClicked(object sender, RoutedEventArgs e)
    {
        _settings.FirstRunCompleted = true;
        AppPathResolver.SaveSettings(_settings);
        DialogResult = true;
        Close();
    }
}
