using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NpuChat;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer breathTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ContextBreath? turnBreathPeak;

    private void BeginBreathTurn()
    {
        turnBreathPeak = null;
        engine?.ResetBreath();
        modelEngine?.ResetBreath();
    }

    private void RememberBreath(ContextBreath? reading)
    {
        if (reading is { Estimated: false }) turnBreathPeak = ContextBreath.Tightest([turnBreathPeak, reading]);
    }

    private void RefreshPhoenixBreath()
    {
        if (closing.IsCancellationRequested || compact || HiddenInTray || BioMonitorPanel.Visibility != Visibility.Visible)
        {
            breathTimer.Stop();
            return;
        }

        var active = new List<ContextBreath?>(WorkerSlotCount + 1);
        var gatekeeper = gatekeeperLight.Active ? engine?.Breath : null;
        SetWorkerBreath(gatekeeperLight, gatekeeper);
        active.Add(gatekeeper);
        for (int index = 0; index < workerSlots.Count; index++)
        {
            ContextBreath? reading = workerSlotOwners[index] switch
            {
                ScheduledWorker worker => worker.Breath,
                { } owner when ReferenceEquals(owner, manualWorker) => modelEngine?.Breath,
                _ => null
            };
            SetWorkerBreath(workerSlots[index], reading);
            active.Add(reading);
        }

        bool working = scheduledJobs.Count > 0 || gatekeeperLight.Active || workerSlotOwners.Contains(manualWorker);
        if (working && !breathTimer.IsEnabled) breathTimer.Start();
        if (!working) breathTimer.Stop();
        var latest = ContextBreath.Tightest(active);
        var display = latest ?? turnBreathPeak;
        SetBreathBar(PhoenixBreathProgress, display);
        PhoenixBreathValue.Text = display is null ? working ? "Measuring…" : "Idle" :
            $"{(display.Estimated ? "~" : "")}{display.Remaining:N0} left";
        string details = display is null ? "Phoenix Breath shows how much context is left in the busiest worker's window." :
            (latest is null ? "Tightest window in the last turn. " : "Tightest active worker window. ") + DescribeBreath(display);
        SetBreathDescription(PhoenixBreathPanel, details);
    }

    private void SetWorkerBreath(WorkerIndicator worker, ContextBreath? reading)
    {
        worker.Breath.Visibility = reading is null ? Visibility.Collapsed : Visibility.Visible;
        SetBreathBar(worker.Breath, reading);
        string details = reading is null ? worker.Description : $"{worker.Description} {DescribeBreath(reading)}";
        SetBreathDescription(worker.Card, details);
    }

    private void SetBreathBar(ProgressBar bar, ContextBreath? reading)
    {
        if (Equals(bar.Tag, reading)) return;
        bar.Tag = reading;
        bar.Value = reading?.Percent ?? 0;
        bar.Foreground = (Brush)Surface.Resources[reading?.Level switch
        {
            BreathLevel.ReserveExceeded => "BreathRed",
            BreathLevel.NearLimit => "Ember",
            _ => "Mint"
        }];
        bar.Background = (Brush)Surface.Resources["Edge"];
    }

    private static string DescribeBreath(ContextBreath reading)
    {
        string status = reading.Level switch
        {
            BreathLevel.ReserveExceeded => "Safety reserve exceeded.",
            BreathLevel.NearLimit => "Near the context limit.",
            _ => "Context has room."
        };
        return $"{(reading.Estimated ? "Estimated" : "Measured")} context. {reading.Used:N0} of {reading.Capacity:N0} tokens used, " +
            $"{reading.Remaining:N0} left, {reading.Reserve:N0} reserved. {status}";
    }

    private static void SetBreathDescription(FrameworkElement element, string details)
    {
        if (Equals(ToolTipService.GetToolTip(element), details)) return;
        ToolTipService.SetToolTip(element, details);
        AutomationProperties.SetHelpText(element, details);
    }
}
