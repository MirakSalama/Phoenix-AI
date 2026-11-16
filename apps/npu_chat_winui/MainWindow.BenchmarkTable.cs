using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NpuChat;

public sealed partial class MainWindow
{
    private FrameworkElement BenchmarkTable(IReadOnlyList<BenchmarkResult> results)
    {
        if (results.Count == 0) return Wrapped("Results will appear here as each pair finishes.");

        var devices = results.Select(row => (Backend: BenchmarkBackend(row), row.Device))
            .Distinct().OrderBy(column => column.Backend).ThenBy(column => column.Device).ToArray();
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(131) });
        foreach (var _ in devices)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(134) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddToTable(grid, 0, 0, TableTile(TableText("MODEL", 11, Muted, true), HeaderFill));
        for (int i = 0; i < devices.Length; i++)
        {
            var (backend, device) = devices[i];
            var (icon, label) = backend switch
            {
                LocalBackend.Npu => ("\uE950", "NPU"),
                LocalBackend.Integrated => ("\uE7F4", "Integrated GPU"),
                LocalBackend.Discrete => ("\uE945", "Discrete GPU"),
                LocalBackend.Cpu => ("\uE9D9", "CPU"),
                _ => ("\uE7F4", "GPU")
            };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            title.Children.Add(new FontIcon { Glyph = icon, FontSize = 15, Foreground = Mint });
            title.Children.Add(TableText(label, 12, Mint, true));
            var header = TableTile(title, HeaderFill);
            ToolTipService.SetToolTip(header, device);
            AutomationProperties.SetName(header, $"{label}, {device}");
            AddToTable(grid, 0, i + 1, header);
        }

        int rowIndex = 1;
        foreach (var model in results.GroupBy(result => result.Model))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = model.First().ModelName;
            var best = model.Where(result => result.Score.HasValue)
                .OrderByDescending(result => result.Score)
                .ThenByDescending(result => result.DecodeRate)
                .ThenBy(result => result.FirstTokenMs)
                .FirstOrDefault();
            var modelLabel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            modelLabel.Children.Add(TableText(name, 14, Light, true));
            var bestLabel = best is null ? "Awaiting score" : $"Top score {best.Score:0.#}";
            modelLabel.Children.Add(TableText(bestLabel, 11, Muted));
            AddToTable(grid, rowIndex, 0, TableTile(modelLabel, HeaderFill));
            for (int i = 0; i < devices.Length; i++)
            {
                var column = devices[i];
                var result = model.FirstOrDefault(item => item.Device == column.Device && BenchmarkBackend(item) == column.Backend);
                var tile = ResultTile(result, ReferenceEquals(result, best) && best is not null);
                AddToTable(grid, rowIndex, i + 1, tile);
            }
            rowIndex++;
        }
        return grid;
    }

    private LocalBackend BenchmarkBackend(BenchmarkResult result) => result.Backend ?? result.Device switch
    {
        "NPU" => LocalBackend.Npu,
        "CPU" => LocalBackend.Cpu,
        _ => gpus.FirstOrDefault(gpu => gpu.Name == result.Device) is { } gpu
            ? gpu.Integrated ? LocalBackend.Integrated : LocalBackend.Discrete
            : LocalBackend.Auto
    };

    private static Border ResultTile(BenchmarkResult? row, bool best)
    {
        var content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var tile = TableTile(content, CellFill);
        tile.MinHeight = 87;
        if (row?.Score is not double score)
        {
            string state = row is null ? "Pending" : row.IsUnavailable ? "Unavailable" : "Error";
            content.Children.Add(TableText(state == "Error" ? "⚠  Error" : "—  " + state, 12,
                state == "Error" ? Amber : Muted, true));
            if (row is not null)
            {
                string detail = row.IsUnavailable ? "Model or runtime" :
                    row.Error?.Contains("visible", StringComparison.OrdinalIgnoreCase) == true ||
                    row.Error?.Contains("empty reply", StringComparison.OrdinalIgnoreCase) == true
                        ? "No visible reply" : "See details";
                content.Children.Add(TableText(detail, 11, Muted));
                ToolTipService.SetToolTip(tile, row.Error);
                AutomationProperties.SetName(tile, $"{row.ModelName}, {row.Device}, {state}. {row.Error}");
            }
            return tile;
        }

        var grade = row.Rating is "A" or "B" ? Mint : row.Rating == "C" ? Sky : Amber;
        var scoreRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        scoreRow.Children.Add(new Border
        {
            Child = TableText(row.Rating, 12, grade, true), Background = HeaderFill,
            Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4)
        });
        scoreRow.Children.Add(TableText($"{score:0.#}/100", 12, Light, true));
        content.Children.Add(scoreRow);
        content.Children.Add(TableText($"{row.DecodeRate:0.#} tok/s", 14, best ? Mint : Light, true));
        content.Children.Add(TableText($"{row.FirstTokenMs:F0} ms  ·  {row.Correct}/3", 11, Muted));
        if (best)
        {
            tile.BorderBrush = Mint;
            tile.BorderThickness = new Thickness(1);
        }
        string summary = $"{row.ModelName} on {row.Device}\nGrade {row.Rating} · {score:0.#}/100\n" +
            $"{row.DecodeRate:0.#} tok/s · {row.FirstTokenMs:F0} ms to first token\n" +
            $"{row.Correct}/3 correct · {row.GeneratedTokens} timed tokens · {row.LoadSeconds:F1}s load" +
            (best ? "\nHighest score for this model. Faster generation breaks ties." : "");
        ToolTipService.SetToolTip(tile, summary);
        AutomationProperties.SetName(tile, summary.Replace('\n', ' '));
        return tile;
    }

    private static Border TableTile(UIElement child, Brush fill) => new()
    {
        Child = child, Background = fill, CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 7, 9, 7)
    };

    private static TextBlock TableText(string value, double size, Brush color, bool bold = false) => new()
    {
        Text = value, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center
    };

    private static void AddToTable(Grid grid, int row, int column, FrameworkElement tile)
    {
        Grid.SetRow(tile, row);
        Grid.SetColumn(tile, column);
        grid.Children.Add(tile);
    }

    private static readonly SolidColorBrush HeaderFill = new(Color.FromArgb(255, 39, 47, 66));
    private static readonly SolidColorBrush CellFill = new(Color.FromArgb(255, 27, 37, 53));
    private static readonly SolidColorBrush Light = new(Color.FromArgb(255, 232, 242, 250));
    private static readonly SolidColorBrush Muted = new(Color.FromArgb(255, 166, 185, 207));
    private static readonly SolidColorBrush Mint = new(Color.FromArgb(255, 102, 230, 190));
    private static readonly SolidColorBrush Sky = new(Color.FromArgb(255, 142, 202, 255));
    private static readonly SolidColorBrush Amber = new(Color.FromArgb(255, 255, 189, 110));
}
