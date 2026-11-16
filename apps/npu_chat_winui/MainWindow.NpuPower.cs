using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NpuChat;

public sealed partial class MainWindow
{
    private readonly NpuPowerController npuPower = new();
    private bool bridgePowerBoost;

    private (StackPanel View, Func<Task> Save) NpuPowerSettings()
    {
        var modes = new PhoenixPicker
        {
            Header = "NPU power mode",
            ItemsSource = new[] { "System setting", "Auto · Boost when needed", "Power saver", "Balanced", "Performance" },
            SelectedIndex = (int)workspace.NpuPower,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = npuAvailable && NpuPowerController.IsAvailable && workspaceUsable &&
                phase is Phase.Ready or Phase.Failed && activeLanProfile is null && currentReply is null
        };
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(modes);
        panel.Children.Add(new TextBlock
        {
            Text = npuAvailable && NpuPowerController.IsAvailable
                ? "Auto temporarily boosts heavier NPU turns while plugged in. This setting applies to the Phoenix PC, including LAN chats."
                : "NPU power controls are unavailable on this PC.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        return (panel, async () =>
        {
            if (!modes.IsEnabled || activeLanProfile is not null || currentReply is not null ||
                phase is not (Phase.Ready or Phase.Failed) ||
                modes.SelectedIndex < 0 || (NpuPowerPolicy)modes.SelectedIndex == workspace.NpuPower) return;
            var previous = workspace.NpuPower;
            var selected = (NpuPowerPolicy)modes.SelectedIndex;
            if (!await npuPower.ApplyAsync(NpuPowerController.ManualMode(selected)))
            {
                await npuPower.ApplyAsync(NpuPowerController.ManualMode(previous));
                throw new InvalidOperationException("AMD could not change the NPU power mode. Check diagnostic logs.");
            }
            workspace.NpuPower = selected;
            if (SaveWorkspace()) return;
            workspace.NpuPower = previous;
            await npuPower.ApplyAsync(NpuPowerController.ManualMode(previous));
            throw new IOException("Could not save the NPU power mode.");
        });
    }

    private bool ShouldBoostNpu(WorkIntent intent, int promptTokens, int outputTokens, bool thinking) =>
        workspace.NpuPower == NpuPowerPolicy.Auto && NpuPowerController.IsAvailable && NpuPowerController.IsPluggedIn &&
        WorkScheduler.NeedsNpuBoost(intent, promptTokens, outputTokens, thinking);

    private async Task StartNpuBoostAsync()
    {
        if (await npuPower.ApplyAsync("performance") && !closing.IsCancellationRequested)
            Record(new("NPU power", "Performance", "Auto selected Performance for this turn."));
    }
}
