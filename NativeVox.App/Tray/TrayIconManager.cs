namespace NativeVox.App.Tray;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Serilog;
using NativeVox.Core.Models;
using NativeVox.Core.Services;
using NativeVox.Infrastructure.Injection;
using NativeVox.Infrastructure.Storage;

public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly IModelManager _modelManager;
    private readonly AppSettings _settings;
    private readonly Action _onToggleDictation;
    private readonly Action<ModelSize> _onModelSelected;
    private readonly Action _onOpenSettings;
    private bool _disposed;

    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _modelMenuItem;

    public TrayIconManager(
        IModelManager modelManager,
        AppSettings settings,
        Action onToggleDictation,
        Action<ModelSize> onModelSelected,
        Action onOpenSettings)
    {
        _modelManager = modelManager;
        _settings = settings;
        _onToggleDictation = onToggleDictation;
        _onModelSelected = onModelSelected;
        _onOpenSettings = onOpenSettings;

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateAppIcon(false),
            Text = "NativeVox - Offline Dictation",
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => _onOpenSettings();
        _notifyIcon.ContextMenuStrip = BuildContextMenu();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("NativeVox: Ready") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var toggleItem = new ToolStripMenuItem($"Toggle Dictation ({_settings.Hotkey})", null, (_, _) => _onToggleDictation());
        menu.Items.Add(toggleItem);

        // Models Submenu
        _modelMenuItem = new ToolStripMenuItem("Model");
        RebuildModelMenu();
        menu.Items.Add(_modelMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => _onOpenSettings()));

        var relaunchAdminItem = new ToolStripMenuItem("Relaunch as Administrator", null, (_, _) => RelaunchAsAdmin());
        if (UIPIElevationDetector.IsCurrentProcessElevated())
        {
            relaunchAdminItem.Enabled = false;
            relaunchAdminItem.Text = "Running as Administrator";
        }
        menu.Items.Add(relaunchAdminItem);

        menu.Items.Add(new ToolStripMenuItem("View Local Logs", null, (_, _) => OpenLogsFolder()));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) =>
        {
            Log.Debug("Exit selected from tray menu.");
            System.Windows.Application.Current.Shutdown();
        }));

        return menu;
    }

    public void RebuildModelMenu()
    {
        if (_modelMenuItem == null) return;
        _modelMenuItem.DropDownItems.Clear();

        var models = _modelManager.GetModels();
        foreach (var m in models)
        {
            var item = new ToolStripMenuItem(m.DisplayName)
            {
                Checked = m.Size == _settings.SelectedModel,
                Enabled = m.IsDownloaded
            };

            var size = m.Size;
            item.Click += (_, _) =>
            {
                Log.Debug("Model {Model} selected from tray menu.", size);
                _onModelSelected(size);
            };
            _modelMenuItem.DropDownItems.Add(item);
        }

        Log.Verbose("Tray model menu rebuilt ({Downloaded}/{Total} models downloaded, active {Active}).",
            models.Count(m => m.IsDownloaded), models.Count, _settings.SelectedModel);
    }

    public void SetRecordingState(bool isRecording)
    {
        Log.Verbose("Tray state -> {State}.", isRecording ? "Recording" : "Ready");
        _notifyIcon.Icon = CreateAppIcon(isRecording);
        if (_statusItem != null)
        {
            _statusItem.Text = isRecording ? "NativeVox: Dictating..." : "NativeVox: Ready";
        }
        _notifyIcon.Text = isRecording ? "NativeVox - Recording (Dictating)" : "NativeVox - Ready (Offline Dictation)";
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        Log.Debug("Tray notification ({Icon}): {Title} - {Message}", icon, title, message);
        _notifyIcon.ShowBalloonTip(3000, title, message, icon);
    }

    public void ShowMicrophonePermissionWarning()
    {
        Log.Debug("Showing microphone permission warning balloon.");
        _notifyIcon.BalloonTipClicked += OnMicPermissionBalloonClicked;
        _notifyIcon.ShowBalloonTip(
            5000,
            "Microphone Access Denied",
            "Windows blocked desktop microphone access. Click here to open Windows Microphone Settings.",
            ToolTipIcon.Warning
        );
    }

    private void OnMicPermissionBalloonClicked(object? sender, EventArgs e)
    {
        _notifyIcon.BalloonTipClicked -= OnMicPermissionBalloonClicked;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:privacy-microphone",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Warning(ex, "Could not open Windows microphone privacy settings."); }
    }

    private static void OpenLogsFolder()
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
        catch (Exception ex) { Log.Warning(ex, "Could not open logs folder."); }
    }

    private static void RelaunchAsAdmin()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        Log.Information("Relaunching as Administrator: {Path}", exePath);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) { Log.Debug("Elevated relaunch cancelled or failed: {Message}", ex.Message); }
    }

    private static Icon CreateAppIcon(bool isRecording)
    {
        // Programmatically generate high-DPI 32x32 system tray icon
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Background circular badge
        var bgBrush = new SolidBrush(isRecording ? Color.FromArgb(239, 68, 68) : Color.FromArgb(37, 99, 235));
        g.FillEllipse(bgBrush, 2, 2, 28, 28);

        // Microphone capsule
        using var whiteBrush = new SolidBrush(Color.White);
        g.FillRectangle(whiteBrush, 12, 7, 8, 11);
        using var whitePen = new Pen(Color.White, 2.5f);
        // Cradle arc
        g.DrawArc(whitePen, 9, 10, 14, 10, 0, 180);
        // Stem
        g.DrawLine(whitePen, 16, 20, 16, 25);
        // Base
        g.DrawLine(whitePen, 12, 25, 20, 25);

        IntPtr hIcon = bitmap.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
