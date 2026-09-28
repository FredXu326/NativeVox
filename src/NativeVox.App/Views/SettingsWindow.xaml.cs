namespace NativeVox.App.Views;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NativeVox.Core.Models;
using NativeVox.Core.Services;
using NativeVox.Infrastructure.Injection;
using NativeVox.Infrastructure.Storage;

public partial class SettingsWindow : Window
{
    private readonly IModelManager _modelManager;
    private readonly AppSettings _settings;
    private readonly Action _onSettingsUpdated;

    public SettingsWindow(IModelManager modelManager, AppSettings settings, Action onSettingsUpdated)
    {
        InitializeComponent();
        _modelManager = modelManager;
        _settings = settings;
        _onSettingsUpdated = onSettingsUpdated;

        LoadData();
    }

    private void LoadData()
    {
        var models = _modelManager.GetModels();
        ModelSelector.ItemsSource = models;
        ModelSelector.DisplayMemberPath = nameof(ModelInfo.DisplayName);
        ModelSelector.SelectedItem = models.FirstOrDefault(m => m.Size == _settings.SelectedModel) ?? models.First();

        ProviderSelector.ItemsSource = Enum.GetValues<ExecutionProvider>();
        ProviderSelector.SelectedItem = _settings.Provider;

        ProgressiveTypingCheckBox.IsChecked = _settings.DirectProgressiveTypingEnabled;
        PulsingHudCheckBox.IsChecked = _settings.EnablePulsingHUD;

        if (UIPIElevationDetector.IsCurrentProcessElevated())
        {
            RelaunchAdminButton.IsEnabled = false;
            RelaunchAdminButton.Content = "Running as Administrator";
        }
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelSelector.SelectedItem is ModelInfo selected)
        {
            bool isDownloaded = _modelManager.IsDownloaded(selected.Size);
            ModelStatusText.Text = isDownloaded ? "Status: Downloaded and ready." : $"Status: Not downloaded ({selected.SizeInMegabytes} MB).";
            DownloadModelButton.Visibility = isDownloaded ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void OnDownloadModelClicked(object sender, RoutedEventArgs e)
    {
        if (ModelSelector.SelectedItem is not ModelInfo selected) return;

        DownloadModelButton.IsEnabled = false;
        DownloadModelButton.Content = "Downloading...";

        try
        {
            await _modelManager.DownloadModelAsync(selected.Size);
            ModelStatusText.Text = "Status: Downloaded and ready.";
            DownloadModelButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to download model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            DownloadModelButton.IsEnabled = true;
            DownloadModelButton.Content = "Download This Model";
        }
    }

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPathResolver.EnsureDirectoriesExist();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPathResolver.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open logs folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRelaunchAdminClicked(object sender, RoutedEventArgs e)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            Process.Start(startInfo);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Elevation cancelled or failed: {ex.Message}", "Relaunch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (ModelSelector.SelectedItem is ModelInfo selected)
        {
            _settings.SelectedModel = selected.Size;
        }

        if (ProviderSelector.SelectedItem is ExecutionProvider provider)
        {
            _settings.Provider = provider;
        }

        _settings.DirectProgressiveTypingEnabled = ProgressiveTypingCheckBox.IsChecked ?? true;
        _settings.EnablePulsingHUD = PulsingHudCheckBox.IsChecked ?? true;

        AppPathResolver.SaveSettings(_settings);
        _onSettingsUpdated();
        Close();
    }
}
