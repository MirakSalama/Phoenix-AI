using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NpuChat;

public sealed partial class MainWindow
{
    private WorkMode desktopWorkMode = WorkMode.Smart;
    private LocalBackend desktopBackend = LocalBackend.Auto;
    private bool desktopThinking;

    private WorkMode SelectedWorkMode => activeLanProfile is null ? desktopWorkMode : WorkMode.Smart;
    private LocalBackend PreferredBackend => activeLanProfile is null ? desktopBackend : LocalBackend.Auto;
    private bool TurnThinkingEnabled => activeLanProfile is null && desktopThinking;

    private (Expander View, Action Apply) DesktopAdvancedSettings()
    {
        bool canApply = phase is Phase.Ready or Phase.Failed || phase == Phase.WarmingModel && !pendingLocalTurn;
        var modelChoices = ModelLibrary.Visible();
        var mode = new PhoenixPicker
        {
            Header = "Scheduling mode", ItemsSource = new[] { "Manual", "Smart Auto", "Parallel", "Parallel same model" },
            SelectedIndex = (int)desktopWorkMode
        };
        var model = new PhoenixPicker { Header = "Chat model" };
        foreach (var choice in modelChoices)
            model.Items.Add(new ComboBoxItem { Content = ModelName(choice), IsEnabled = ModelAvailable(choice) });
        model.SelectedIndex = Array.IndexOf(modelChoices, selectedModel);
        var device = new PhoenixPicker
        {
            Header = "Preferred device", ItemsSource = new[] { "Auto", "NPU", "iGPU", "dGPU", "CPU" },
            SelectedIndex = (int)desktopBackend
        };
        var thinking = new CheckBox { Content = "Thinking mode", IsChecked = desktopThinking };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "These overrides apply to Windows chats. LAN chats use Smart Auto.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(mode);
        panel.Children.Add(model);
        panel.Children.Add(device);
        panel.Children.Add(thinking);

        void Update()
        {
            bool automatic = (WorkMode)mode.SelectedIndex is WorkMode.Smart or WorkMode.Parallel;
            model.IsEnabled = !automatic;
            thinking.IsEnabled = automatic || model.SelectedIndex >= 0 && SupportsThinking(modelChoices[model.SelectedIndex]);
        }
        mode.SelectionChanged += (_, _) => Update();
        model.SelectionChanged += (_, _) => Update();
        Update();

        void Apply()
        {
            if (!canApply || activeLanProfile is not null || currentReply is not null) return;
            var nextMode = (WorkMode)mode.SelectedIndex;
            var nextModel = model.SelectedIndex >= 0 ? modelChoices[model.SelectedIndex] : selectedModel;
            var nextDevice = (LocalBackend)device.SelectedIndex;
            bool nextThinking = thinking.IsEnabled && thinking.IsChecked == true;
            if (nextMode == desktopWorkMode && nextModel == selectedModel &&
                nextDevice == desktopBackend && nextThinking == desktopThinking) return;
            desktopWorkMode = nextMode;
            desktopBackend = nextDevice;
            desktopThinking = nextThinking;
            StopSelectedModel();
            ReleaseWarmWorker();
            SetModelSelection(nextModel);
            if (phase is Phase.Ready or Phase.Failed or Phase.WarmingModel)
            {
                if (desktopWorkMode == WorkMode.Manual) RouteLocal();
                else SetPhase(Phase.Ready);
            }
        }

        return (new Expander { Header = "Advanced · Windows chat", Content = panel, IsEnabled = canApply,
            HorizontalAlignment = HorizontalAlignment.Stretch }, Apply);
    }
}
