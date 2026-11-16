using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;

namespace NpuChat;

public sealed partial class MainWindow
{
    private LanTrayIcon? lanTray;
    private bool quittingFromTray;
    private bool resumeDeviceMonitor;
    private ContentDialog? activeSettingsDialog;

    private bool HiddenInTray => lanTray is not null;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (lanServer is null || quittingFromTray) return;
        args.Cancel = true;
        if (HiddenInTray) return;

        var tray = new LanTrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Phoenix.ico"));
        if (!tray.Show())
        {
            tray.Dispose();
            DiagnosticLog.Write("lan", "Could not add the notification-area icon; window remains visible.");
            FullStatus.Text = "Couldn’t open the system tray. LAN access is still running.";
            return;
        }
        lanTray = tray;
        resumeDeviceMonitor = monitorTimer.IsEnabled;
        monitorTimer.Stop();
        workerPulse.Stop();
        modelTimer.Stop();
        ShareButton.Flyout.Hide();
        activeSettingsDialog?.Hide();
        SetVoiceLoop(false);
        UpdateOrb();
        sender.Hide();
        DiagnosticLog.Write("lan", "Window hidden in the system tray; LAN access is still running.");
    }

    private bool HandleTrayMessage(uint message, nuint wParam, nint lParam)
    {
        if (lanTray is not { } tray) return false;
        if (message == LanTrayIcon.TaskbarCreated)
        {
            if (!tray.Show()) DispatcherQueue.TryEnqueue(RestoreFromTray);
            return false;
        }
        if (message != LanTrayIcon.CallbackMessage) return false;
        switch (tray.Command(wParam, lParam))
        {
            case LanTrayIcon.OpenCommand:
                DispatcherQueue.TryEnqueue(RestoreFromTray);
                break;
            case LanTrayIcon.QuitCommand:
                DispatcherQueue.TryEnqueue(() =>
                {
                    quittingFromTray = true;
                    Close();
                });
                break;
        }
        return true;
    }

    private void RestoreFromTray()
    {
        if (!HiddenInTray || closing.IsCancellationRequested) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        RemoveLanTray();
        if (resumeDeviceMonitor) monitorTimer.Start();
        if (phase == Phase.WarmingModel)
        {
            modelTimer.Start();
            ShowModelProgress();
        }
        UpdateOrb();
        UpdateWorkerPanel();
        Activate();
        ScrollToEnd();
        DiagnosticLog.Write("lan", "Restored the window from the system tray.");
    }

    private void RemoveLanTray()
    {
        lanTray?.Dispose();
        lanTray = null;
    }
}
