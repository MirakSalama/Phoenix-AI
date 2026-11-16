using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace NpuChat;

public sealed partial class MainWindow
{
    private const int WorkerSlotCount = WorkScheduler.MaxWorkers;
    private readonly DispatcherTimer workerPulse = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly UISettings workerUiSettings = new();
    private readonly List<WorkerIndicator> workerSlots = [];
    private readonly object?[] workerSlotOwners = new object?[WorkerSlotCount];
    private readonly object manualWorker = new();
    private WorkerIndicator routerLight = null!;
    private WorkerIndicator gatekeeperLight = null!;
    private WorkerIndicator dictationLight = null!;
    private bool routerInstalled;
    private bool decisionActive;
    private string? dictationDevice;
    private readonly List<Microsoft.UI.Xaml.Shapes.Path> workerBranches = [];
    private const double WorkerCardHeight = 46;
    private const double WorkerBranchHeight = 8;
    private const double WorkerCardGap = 6;
    private int workerColumns;
    private static readonly IReadOnlyDictionary<string, string> WorkerIcons = new Dictionary<string, string>
    {
        ["brain"] = "M8,3 C6,0 2,2 2,5 C0,6 0,9 2,10 C1,13 5,15 8,13 C11,15 15,13 14,10 C16,9 16,6 14,5 C14,2 10,0 8,3 L8,13 M4,5 C4,7 6,8 8,7 M12,5 C12,7 10,8 8,7 M4,11 L6,10 M12,11 L10,10",
        ["lfm"] = "M2,3 L6,13 L8,7 L10,13 L14,3 M4,3 L6,7 M12,3 L10,7",
        ["gemma"] = "M8,1 L10,6 L15,8 L10,10 L8,15 L6,10 L1,8 L6,6 Z",
        ["model"] = "M2,4 L8,1 L14,4 L14,12 L8,15 L2,12 Z M2,4 L8,7 L14,4 M8,7 L8,15",
        ["mic"] = "M6,3 C6,0 10,0 10,3 L10,7 C10,10 6,10 6,7 Z M3,6 L3,7 C3,14 13,14 13,7 L13,6 M8,12 L8,15 M5,15 L11,15",
        ["CPU"] = "M4,4 L12,4 L12,12 L4,12 Z M6,6 L10,6 L10,10 L6,10 Z M6,1 L6,4 M10,1 L10,4 M6,12 L6,15 M10,12 L10,15 M1,6 L4,6 M1,10 L4,10 M12,6 L15,6 M12,10 L15,10",
        ["NPU"] = "M3,3 L13,3 L13,13 L3,13 Z M5,10 L8,6 L11,10 M8,6 L8,12 M1,6 L3,6 M1,10 L3,10 M13,6 L15,6 M13,10 L15,10",
        ["iGPU"] = "M1,2 L15,2 L15,11 L1,11 Z M8,11 L8,14 M5,14 L11,14 M4,8 L7,5 L10,8 L12,6",
        ["dGPU"] = "M2,3 L15,3 L15,12 L2,12 Z M2,1 L2,14 M5,12 L5,14 L11,14 L11,12 M11,7.5 A2.5,2.5 0 1 0 6,7.5 A2.5,2.5 0 1 0 11,7.5"
    };

    private static void SetWorkerIcon(Border container, string? key, Brush stroke)
    {
        if (!Equals(container.Tag, key))
        {
            container.Tag = key;
            container.Child = key is not null && WorkerIcons.TryGetValue(key, out var data)
                ? (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
                    $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Stretch='Uniform' Data='{data}' />")
                : null;
        }
        container.Visibility = container.Child is null ? Visibility.Collapsed : Visibility.Visible;
        if (container.Child is Microsoft.UI.Xaml.Shapes.Path path) path.Stroke = stroke;
    }

    private sealed record WorkerIndicator(Border Card, Border Icon, Border Hardware, TextBlock Name, TextBlock Status,
        TextBlock ThinkingLabel, Ellipse Light, ProgressBar Breath)
    {
        internal bool Active { get; set; }
        internal bool Thinking { get; set; }
        internal string Description { get; set; } = "";
    }

    private void InitializeWorkerPanel()
    {
        routerLight = CreateWorkerIndicator();
        gatekeeperLight = CreateWorkerIndicator();
        dictationLight = CreateWorkerIndicator();
        var services = new[] { gatekeeperLight, routerLight, dictationLight };
        for (int column = 0; column < services.Length; column++)
        {
            Grid.SetColumn(services[column].Card, column);
            WorkerServices.Children.Add(services[column].Card);
        }

        for (int index = 0; index < WorkerSlotCount; index++)
        {
            var branch = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = 1 };
            workerBranches.Add(branch);
            WorkerConnections.Children.Add(branch);
            var indicator = CreateWorkerIndicator();
            workerSlots.Add(indicator);
            WorkerSlots.Children.Add(indicator.Card);
        }
        LayoutWorkerSlots(2);
        workerPulse.Tick += (_, _) => AnimateWorkerLights();
        breathTimer.Tick += (_, _) => RefreshPhoenixBreath();
        UpdateWorkerPanel();
    }

    private static WorkerIndicator CreateWorkerIndicator()
    {
        var icon = new Border { Width = 13, Height = 13 };
        var hardware = new Border { Width = 10, Height = 10 };
        var icons = new StackPanel
        {
            Width = 14, Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { icon, hardware }
        };
        var name = new TextBlock { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
        var thinking = new TextBlock { FontSize = 10, Text = "Thinking", Visibility = Visibility.Collapsed };
        var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name, status, thinking } };
        var light = new Ellipse { Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        var contents = new Grid { ColumnSpacing = 4 };
        contents.RowDefinitions.Add(new RowDefinition());
        contents.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contents.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contents.ColumnDefinitions.Add(new ColumnDefinition());
        contents.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contents.Children.Add(icons);
        Grid.SetColumn(labels, 1);
        contents.Children.Add(labels);
        Grid.SetColumn(light, 2);
        contents.Children.Add(light);
        var breath = new ProgressBar { Height = 3, MinHeight = 0, Maximum = 100,
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(breath, 1);
        Grid.SetColumnSpan(breath, 3);
        contents.Children.Add(breath);
        AutomationProperties.SetAccessibilityView(contents, AccessibilityView.Raw);
        var card = new Border
        {
            Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), Child = contents
        };
        AutomationProperties.SetAccessibilityView(card, AccessibilityView.Content);
        return new(card, icon, hardware, name, status, thinking, light, breath);
    }

    private void LayoutWorkerSlots(int columns)
    {
        if (workerColumns == columns) return;
        workerColumns = columns;
        WorkerSlots.ColumnDefinitions.Clear();
        WorkerSlots.RowDefinitions.Clear();
        int rows = (WorkerSlotCount + columns - 1) / columns;
        for (int col = 0; col < columns; col++)
        {
            if (col > 0) WorkerSlots.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WorkerCardGap) });
            WorkerSlots.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int row = 0; row < rows; row++)
        {
            WorkerSlots.RowDefinitions.Add(new RowDefinition { Height = new GridLength(row == 0 ? WorkerBranchHeight : WorkerCardGap) });
            WorkerSlots.RowDefinitions.Add(new RowDefinition { Height = new GridLength(WorkerCardHeight) });
        }
        Grid.SetColumnSpan(WorkerConnections, columns * 2 - 1);
        Grid.SetRowSpan(WorkerConnections, rows * 2);
        for (int index = 0; index < workerSlots.Count; index++)
        {
            Grid.SetColumn(workerSlots[index].Card, 2 * (index % columns));
            Grid.SetRow(workerSlots[index].Card, 1 + 2 * (index / columns));
        }
    }

    private void WorkerSlots_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width;
        if (width <= 0 || workerBranches.Count != WorkerSlotCount) return;
        LayoutWorkerSlots(width >= 474 ? 4 : 2);
        double center = width / 2;
        double cardWidth = (width - (workerColumns - 1) * WorkerCardGap) / workerColumns;

        for (int index = 0; index < workerBranches.Count; index++)
        {
            int col = index % workerColumns;
            bool secondRow = index >= workerColumns;
            double x = cardWidth / 2 + col * (cardWidth + WorkerCardGap);
            var branches = new PointCollection();
            if (secondRow)
            {
                double y = WorkerBranchHeight + WorkerCardHeight + WorkerCardGap / 2;
                branches.Add(new(center, y));
                branches.Add(new(x, y));
                branches.Add(new(x, y + WorkerCardGap / 2));
            }
            else
            {
                branches.Add(new(center, WorkerBranchHeight / 2));
                branches.Add(new(x, WorkerBranchHeight / 2));
                branches.Add(new(x, WorkerBranchHeight));
            }
            var figure = new PathFigure { StartPoint = new(center, 0) };
            figure.Segments.Add(new PolyLineSegment { Points = branches });
            workerBranches[index].Data = new PathGeometry { Figures = { figure } };
        }
    }

    private void SetDecisionActivity(bool active)
    {
        decisionActive = active;
        UpdateWorkerPanel();
    }

    private void TrackWorker(ScheduledWorker worker, string role, bool thinking)
    {
        worker.ResetBreath();
        scheduledJobs.Add(worker, (role, thinking));
        UpdateWorkerPanel();
    }

    private void UntrackWorker(ScheduledWorker worker)
    {
        RememberBreath(worker.MeasuredPeak);
        scheduledJobs.Remove(worker);
        UpdateWorkerPanel();
    }

    private void UpdateWorkerPanel()
    {
        if (workerSlots.Count == 0 || closing.IsCancellationRequested || HiddenInTray || compact)
        {
            workerPulse.Stop();
            breathTimer.Stop();
            return;
        }
        bool routerReady = routerInstalled && workspace.UseDecisionRouter && !decisionUnavailable;
        bool gatekeeperActive = npuAvailable && (lookupNarration is not null ||
            phase == Phase.Thinking && currentReply is not null && !secondaryTurn &&
            lookupTurn is null && scheduledTurn is null && !cloudReply &&
            TurnModel == LocalModel.Lfm230 && activeBackend == LocalBackend.Npu);
        bool dictationActive = dictationAvailable && phase == Phase.Transcribing;
        string routerState = decisionActive ? "Routing" : routerReady ? "Ready" : "Unavailable";
        string gatekeeperState = gatekeeperActive ? "Replying" : npuAvailable ? "Ready" : "Unavailable";
        bool listening = phase == Phase.Listening;
        string dictationState = dictationActive ? "Transcribing" : listening ? "Listening" : dictationAvailable ? "Ready" : "Unavailable";
        SetIndicator(routerLight, "brain", "D1 router", routerState, decisionActive, device: "CPU");
        SetIndicator(gatekeeperLight, ModelGlyph(LocalModel.Lfm230), "Gatekeeper", gatekeeperState, gatekeeperActive, device: "NPU");
        SetIndicator(dictationLight, "mic", "Parakeet", dictationState, dictationActive, listening: listening, device: dictationDevice);

        bool manualActive = activeBackend != LocalBackend.Auto && (phase == Phase.WarmingModel ||
            modelEngine is not null && phase == Phase.Thinking && currentReply is not null && scheduledTurn is null && lookupTurn is null && !cloudReply && secondaryTurn);
        for (int index = 0; index < workerSlotOwners.Length; index++)
        {
            object? owner = workerSlotOwners[index];
            if (owner is ScheduledWorker worker && !scheduledJobs.ContainsKey(worker) ||
                ReferenceEquals(owner, manualWorker) && !manualActive) workerSlotOwners[index] = null;
        }
        foreach (var worker in scheduledJobs.Keys) AssignWorkerSlot(worker);
        if (manualActive) AssignWorkerSlot(manualWorker);

        int count = workerSlotOwners.Count(owner => owner is not null);
        var edge = (Brush)Surface.Resources["Edge"];
        WorkerCount.Text = $"{count} of {WorkerSlotCount} active";
        for (int index = 0; index < workerSlots.Count; index++)
        {
            var indicator = workerSlots[index];
            if (workerSlotOwners[index] is ScheduledWorker worker && scheduledJobs.TryGetValue(worker, out var job))
                ShowWorker(indicator, worker.Target.Model, worker.Target.ShortDevice,
                    worker.Capacity == 0 ? "Loading" : job.Role, job.Thinking && worker.Capacity > 0);
            else if (ReferenceEquals(workerSlotOwners[index], manualWorker))
                ShowWorker(indicator, TurnModel, WorkerDevice(activeBackend), phase == Phase.WarmingModel ? "Loading" : "Answer",
                    phase == Phase.Thinking && ThinkingRequested);
            else
                SetIndicator(indicator, null, $"Worker {index + 1}", "Idle", false);
            var signal = indicator.Active ? (Brush)Surface.Resources[indicator.Thinking ? "Ember" : "Mint"] : edge;
            workerBranches[index].Stroke = signal;
        }

        if (BioMonitorPanel.Visibility == Visibility.Visible &&
            (count > 0 || decisionActive || gatekeeperActive || dictationActive) && workerUiSettings.AnimationsEnabled)
        {
            if (!workerPulse.IsEnabled) workerPulse.Start();
        }
        else workerPulse.Stop();
        AnimateWorkerLights();
        RefreshPhoenixBreath();
    }

    private void AssignWorkerSlot(object owner)
    {
        if (Array.IndexOf(workerSlotOwners, owner) >= 0) return;
        int free = Array.IndexOf(workerSlotOwners, null);
        if (free >= 0) workerSlotOwners[free] = owner;
    }

    private void ShowWorker(WorkerIndicator indicator, LocalModel model, string device, string role, bool thinking)
    {
        SetIndicator(indicator, ModelGlyph(model), ModelName(model), role, true, thinking, device: device);
    }

    private void SetIndicator(WorkerIndicator indicator, string? glyph, string name, string status, bool active,
        bool thinking = false, bool listening = false, string? device = null)
    {
        indicator.Active = active;
        indicator.Thinking = thinking;
        var accent = (Brush)Surface.Resources[thinking ? "Ember" : "Mint"];
        var muted = (Brush)Surface.Resources["Muted"];
        var foreground = active || listening ? accent : muted;
        SetWorkerIcon(indicator.Icon, glyph, foreground);
        SetWorkerIcon(indicator.Hardware, device, foreground);
        indicator.Name.Text = name;
        indicator.Name.Opacity = glyph is null ? 0.74 : 1;
        indicator.Status.Text = device is null ? status : $"{device} · {status}";
        indicator.Status.Foreground = active || listening ? accent : muted;
        indicator.ThinkingLabel.Foreground = accent;
        indicator.ThinkingLabel.Visibility = thinking ? Visibility.Visible : Visibility.Collapsed;
        indicator.Light.Fill = foreground;
        indicator.Light.Opacity = active || listening ? 1 : 0.35;
        indicator.Card.Background = (Brush)Surface.Resources[active ? thinking ? "ThinkingTile" : "ActiveTile" : "Panel"];
        indicator.Card.BorderBrush = active || listening ? accent : (Brush)Surface.Resources["Edge"];
        indicator.Card.BorderThickness = new Thickness(active || listening ? 1 : 0);
        string description = $"{name}. {indicator.Status.Text}" + (thinking ? ". Thinking mode." : ".");
        AutomationProperties.SetName(indicator.Card, description);
        indicator.Description = description;
        if (!ReferenceEquals(indicator, gatekeeperLight) && !workerSlots.Contains(indicator))
            ToolTipService.SetToolTip(indicator.Card, description);
    }

    private void AnimateWorkerLights()
    {
        bool animate = workerPulse.IsEnabled && workerUiSettings.AnimationsEnabled;
        if (!animate) workerPulse.Stop();
        double time = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        Pulse(routerLight, 0);
        Pulse(gatekeeperLight, 1);
        Pulse(dictationLight, 2);
        for (int index = 0; index < workerSlots.Count; index++) Pulse(workerSlots[index], index + 3);

        void Pulse(WorkerIndicator indicator, int offset)
        {
            if (!indicator.Active)
            {
                indicator.Icon.Opacity = indicator.Hardware.Opacity = 1;
                return;
            }
            double phase = time * (indicator.Thinking ? 6.5 : 4.4) + offset * 0.7;
            indicator.Light.Opacity = indicator.Icon.Opacity = animate ? 0.78 + 0.22 * Math.Sin(phase) : 1;
            indicator.Hardware.Opacity = animate ? 0.78 + 0.22 * Math.Sin(phase + 1.8) : 1;
        }
    }

    private static string ModelGlyph(LocalModel model) => model switch
    {
        LocalModel.Gemma => "gemma",
        LocalModel.Lfm230 or LocalModel.Lfm350 or LocalModel.Lfm3 => "lfm",
        _ => "model"
    };

    private static string WorkerDevice(LocalBackend backend) => backend switch
    {
        LocalBackend.Integrated => "iGPU",
        LocalBackend.Discrete => "dGPU",
        LocalBackend.Npu => "NPU",
        LocalBackend.Cpu => "CPU",
        _ => "—"
    };
}
