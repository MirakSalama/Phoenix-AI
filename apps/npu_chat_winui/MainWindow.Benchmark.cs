using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NpuChat;

public sealed partial class MainWindow
{
    private async void Benchmark_Click(object sender, RoutedEventArgs args)
    {
        if (settingsOpen || phase is not (Phase.Ready or Phase.Failed) || string.IsNullOrEmpty(project)) return;
        settingsOpen = true;
        CancellationTokenSource? runCancel = null;
        var start = IconAction("\uE768", "Run benchmark");
        var stop = IconAction("\uE71A", "Cancel benchmark");
        stop.IsEnabled = false;
        var status = TableText("Run the benchmark to compare the installed models on this PC.", 13, Light, true);
        var reportView = new ScrollViewer
        {
            MaxHeight = 401,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Enabled
        };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1 };
        var panel = new Grid { Width = Math.Clamp(Surface.ActualWidth - 110, 300, 728), RowSpacing = 9 };
        FrameworkElement[] rows =
        {
            TableText("Compare scores, speed, first-token latency, and answer checks. Chat pauses during the run.", 12, Muted),
            ButtonRow(start, stop), progress, status,
            reportView,
            TableText("Score = 60 answer checks + 30 speed + 10 latency. The outline marks the top result per model, with speed breaking ties.", 11, Muted)
        };
        foreach (var row in rows)
        {
            Grid.SetRow(row, panel.RowDefinitions.Count);
            panel.RowDefinitions.Add(new RowDefinition { Height = row == reportView ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            panel.Children.Add(row);
        }
        var dialog = WorkspaceDialog("Model benchmark", panel);
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;
        dialog.Resources["ContentDialogMaxHeight"] = 700.0;
        dialog.Closing += (_, eventArgs) =>
        {
            if (runCancel is null) return;
            eventArgs.Cancel = true;
            runCancel.Cancel();
            stop.IsEnabled = false;
            status.Text = "Stopping benchmark and restoring chat…";
        };
        stop.Click += (_, _) =>
        {
            runCancel?.Cancel();
            stop.IsEnabled = false;
            status.Text = "Stopping benchmark and restoring chat…";
        };

        void Render(BenchmarkReport report)
        {
            reportView.Content = BenchmarkTable(report.Results);
        }

        static string Summary(BenchmarkReport report)
        {
            int scored = report.Results.Count(row => row.Score.HasValue);
            int unavailable = report.Results.Count(row => row.IsUnavailable);
            int errors = report.Results.Count - scored - unavailable;
            return $"{scored} scored · {unavailable} unavailable · {errors} errors";
        }

        try
        {
            if (ModelBenchmark.ReadReport() is { } previous)
            {
                Render(previous);
                status.Text = Summary(previous) + (previous.Canceled ? " · Partial run" : " · Last run");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Write("benchmark", $"Previous report unavailable: {error.GetType().Name}");
        }

        start.Click += async (_, _) =>
        {
            if (runCancel is not null || phase is not (Phase.Ready or Phase.Failed)) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            runCancel = cancellation;
            start.IsEnabled = false;
            stop.IsEnabled = true;
            var report = new BenchmarkReport(DateTimeOffset.UtcNow, false, []);
            bool interrupted = false;
            var targets = BenchmarkTargets().ToArray();
            reportView.Content = null;
            progress.Maximum = targets.Length;
            progress.Value = 0;
            try
            {
                SetVoiceLoop(false);
                ReleaseWarmWorker();
                SetPhase(Phase.Benchmarking);
                var bridge = engine;
                var selected = modelEngine;
                ++bridgeId;
                ++modelWorkerId;
                engine = null;
                modelEngine = null;
                activeBackend = LocalBackend.Auto;
                await Task.WhenAll(bridge?.StopAsync() ?? Task.CompletedTask, selected?.StopAsync() ?? Task.CompletedTask);

                for (int i = 0; i < targets.Length; i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var target = targets[i];
                    string name = $"{i + 1}/{targets.Length} · {target.ModelName} · {target.Device}";
                    status.Text = $"{name} · Loading";
                    var result = await ModelBenchmark.RunAsync(project, target,
                        stage => status.Text = $"{name} · {stage}", cancellation.Token);
                    report.Results.Add(result);
                    Render(report);
                    progress.Value = i + 1;
                }
                status.Text = Summary(report);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                interrupted = true;
                if (!closing.IsCancellationRequested) status.Text = "Canceled. Completed results are still available.";
            }
            catch (Exception error)
            {
                interrupted = true;
                DiagnosticLog.Write("benchmark", $"Run stopped: {error}");
                status.Text = "Benchmark stopped: " + error.Message;
            }
            finally
            {
                runCancel = null;
                if (!closing.IsCancellationRequested)
                {
                    try
                    {
                        if (report.Results.Count > 0) ModelBenchmark.SaveReport(report with { Canceled = interrupted });
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        DiagnosticLog.Write("benchmark", $"Could not save report: {error.GetType().Name}");
                        status.Text += " Results could not be saved to disk.";
                    }
                    stop.IsEnabled = false;
                    start.IsEnabled = true;
                    SetPhase(Phase.Starting, "Restoring chat after benchmark…");
                    StartBridge(voiceOnly: !LocalEngine.HasNpuRuntime);
                }
            }
        };

        try { await dialog.ShowAsync(); }
        finally { settingsOpen = false; }
    }

    private IEnumerable<BenchmarkTarget> BenchmarkTargets()
    {
        foreach (var model in ModelLibrary.Visible())
        {
            string name = ModelName(model);
            if (LocalEngine.HasNpuRuntime && LocalEngine.SupportsNpu(model))
                yield return new BenchmarkTarget(model, name, "NPU", null, false,
                    model == LocalModel.Lfm230 || LocalEngine.IsVerified(project, model));
            bool hasGguf = LocalEngine.HasGpuModel(project, model);
            foreach (var gpu in gpus)
                yield return new BenchmarkTarget(model, name, gpu.Name, gpu, false, hasGguf);
            yield return new BenchmarkTarget(model, name, "CPU", null, true, hasGguf);
        }
    }
}
