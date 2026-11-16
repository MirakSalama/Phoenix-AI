using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NpuChat;

public sealed partial class MainWindow
{
    private readonly DeviceMonitor monitor = new();
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<LocalBackend, (Border Capsule, TextBlock Percent, ColumnDefinition Used, ColumnDefinition Available)> gauges = [];
    private bool readingDevices;

    private void InitializeDeviceGauges()
    {
        foreach (var (backend, name) in new[] { (LocalBackend.Cpu, "CPU"), (LocalBackend.Npu, "NPU"),
            (LocalBackend.Integrated, "iGPU"), (LocalBackend.Discrete, "dGPU") })
        {
            var percent = new TextBlock { Text = "—", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
            var header = new Grid { Children =
                { new TextBlock { Text = name, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, percent } };
            var used = new ColumnDefinition { Width = new GridLength(0) };
            var available = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = (Brush)Surface.Resources["Mint"] };
            var bar = new Grid { ColumnDefinitions = { used, available }, Children = { fill } };
            var track = new Border
            {
                Height = 6, CornerRadius = new CornerRadius(3),
                Background = (Brush)Surface.Resources["Edge"], Child = bar
            };
            var capsule = new Border
            {
                Padding = new Thickness(9, 1, 9, 1),
                BorderBrush = (Brush)Surface.Resources["Edge"],
                BorderThickness = new Thickness(gauges.Count % 2, 0, 0, 0),
                Child = new StackPanel { Spacing = 4, Children = { header, track } }
            };
            AutomationProperties.SetAccessibilityView(capsule, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
            Grid.SetColumn(capsule, gauges.Count % 2);
            Grid.SetRow(capsule, gauges.Count / 2);
            DeviceGauges.Children.Add(capsule);
            gauges[backend] = (capsule, percent, used, available);
        }
        monitorTimer.Tick += async (_, _) => await RefreshDeviceGaugesAsync();
        monitorTimer.Start();
    }

    private async void ToggleBioMonitor_Click(object sender, RoutedEventArgs args)
    {
        bool visible = BioMonitorToggle.IsChecked == true;
        BioMonitorPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(BioMonitorToggle, visible ? "Hide Phoenix Bio Mon" : "Show Phoenix Bio Mon");
        UpdateWorkerPanel();
        if (visible) await RefreshDeviceGaugesAsync();
    }

    private void StopDeviceMonitor()
    {
        monitorTimer.Stop();
        breathTimer.Stop();
        monitor.Dispose();
    }

    private async Task RefreshDeviceGaugesAsync()
    {
        if (readingDevices || !startupComplete || compact || HiddenInTray ||
            BioMonitorPanel.Visibility == Visibility.Collapsed) return;
        readingDevices = true;
        try
        {
            var sample = await Task.Run(monitor.Read);
            if (closing.IsCancellationRequested || HiddenInTray) return;
            Update(LocalBackend.Cpu, "CPU", sample.Cpu);
            Update(LocalBackend.Npu, "NPU", sample.Accelerators.GetValueOrDefault(DeviceMonitor.NpuKey));
            Update(LocalBackend.Integrated, "iGPU", GpuReading(LocalBackend.Integrated));
            Update(LocalBackend.Discrete, "dGPU", GpuReading(LocalBackend.Discrete));

            DeviceReading? GpuReading(LocalBackend backend) => GpuFor(backend) is { } gpu
                ? sample.Accelerators.GetValueOrDefault(DeviceMonitor.AdapterKey(gpu.Name)) : null;

            void Update(LocalBackend backend, string name, DeviceReading? reading)
            {
                var (capsule, percentText, fill, track) = gauges[backend];
                var jobs = scheduledJobs.Where(job => job.Key.Target.Backend == backend).ToArray();
                string? work = jobs.Length > 0 ? string.Join(", ", jobs.Select(job =>
                    $"{job.Value.Role}: {ModelName(job.Key.Target.Model)}")) : null;
                bool manualActive = scheduledTurn is null && activeBackend == backend && phase == Phase.Thinking && !cloudReply;
                bool voiceActive = backend == LocalBackend.Npu && npuAvailable && phase == Phase.Transcribing;
                bool loaded = backend == LocalBackend.Npu && npuAvailable ||
                    warmScheduled?.Target.Backend == backend || modelEngine is not null && activeBackend == backend;
                string state = jobs.Any(job => job.Key.Capacity == 0) ? "Loading" :
                    work is not null || manualActive || voiceActive || reading?.AppUtilization >= 1 ? "Working" : loaded ? "Loaded" : "Idle";
                if (backend == LocalBackend.Cpu && decisionActive) state = "Routing";
                string usage = reading?.Utilization is double percent ? $"{percent:F0}%" : "—";
                string clock = reading?.ClockMhz is double mhz ? $"{mhz / 1000:F2} GHz" : "—";
                bool shared = backend is LocalBackend.Integrated or LocalBackend.Npu;
                string memoryName = shared ? "Shared RAM" : backend == LocalBackend.Discrete ? "VRAM" : "System RAM";
                double? used = shared ? reading?.SharedMemoryUsed : reading?.MemoryUsed;
                double? capacity = shared ? reading?.SharedMemoryCapacity : reading?.MemoryCapacity;
                double? appMemory = shared ? reading?.AppSharedMemory : reading?.AppMemory;
                string memory = MemoryAmount(used, capacity);
                percentText.Text = usage;
                double utilization = Math.Clamp(reading?.Utilization ?? 0, 0, 100);
                fill.Width = new GridLength(utilization, GridUnitType.Star);
                track.Width = new GridLength(100 - utilization, GridUnitType.Star);
                string details = $"{name} · {state}\nSystem utilization: {usage}\nClock: {clock}\n{memoryName}: {memory}";
                if (used is double bytes && capacity is > 0)
                    details += $" ({100 * bytes / capacity.Value:F0}%)";
                if (reading?.AppUtilization is double app)
                    details += $"\nPhoenix utilization: {app:F1}%";
                if (appMemory is double appBytes)
                    details += backend == LocalBackend.Cpu
                        ? $"\nPhoenix private RAM: {MemoryAmount(appBytes, null)}"
                        : $"\nPhoenix {memoryName}: {MemoryAmount(appBytes, null)}";
                if (backend == LocalBackend.Discrete && reading?.SharedMemoryUsed is double sharedBytes)
                    details += $"\nShared RAM: {MemoryAmount(sharedBytes, reading.SharedMemoryCapacity)}";
                if (shared && reading?.MemoryCapacity is > 0)
                    details += $"\nReserved memory: {MemoryAmount(reading.MemoryUsed, reading.MemoryCapacity)}";
                if (work is not null) details += "\n" + work;
                ToolTipService.SetToolTip(capsule, details);
                AutomationProperties.SetName(capsule, $"{name} · {usage} · {state} · {clock} · {memoryName} {memory}");
                AutomationProperties.SetHelpText(capsule, details);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
                                      System.ComponentModel.Win32Exception or ArgumentException)
        {
            DiagnosticLog.Write("hardware", $"Live monitor unavailable: {error.GetType().Name}");
            monitorTimer.Stop();
        }
        finally { readingDevices = false; }
    }

    private static string MemoryAmount(double? used, double? capacity)
    {
        if (used is null) return "—";
        const double gibibyte = 1024 * 1024 * 1024;
        return capacity is > 0 ? $"{used / gibibyte:F1}/{capacity / gibibyte:F1} GB" : $"{used / gibibyte:F1} GB";
    }
}
