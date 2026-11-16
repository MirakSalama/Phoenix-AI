using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NpuChat;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, string> modelUpdateResults = new(StringComparer.Ordinal);

    private static ScrollViewer ModelScroll(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private FrameworkElement BuildModelCatalog(Action<bool> busy)
    {
        var search = new TextBox { PlaceholderText = "Search Hugging Face · e.g. coder under 1 GB", MaxLength = 160 };
        var band = new PhoenixPicker
        {
            Header = "Model size",
            ItemsSource = new[] { "Any size", "Under 600M", "600M to under 1B", "1B to under 10B", "10B and up" },
            SelectedIndex = 0
        };
        var refresh = ToolButton("\uE72C", "Refresh");
        var reset = ToolButton("\uE777", "Reset");
        var find = ToolButton("\uE721", "Search");
        var more = ToolButton("\uE712", "More results");
        more.Visibility = Visibility.Collapsed;
        var status = ToolText("Search the Hub or choose a suggestion.", muted: true);
        var results = new StackPanel { Spacing = 6 };
        var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var suggestions = new StackPanel { Spacing = 8 };
        var recommended = new Expander { Header = "Suggested models", Content = suggestions, HorizontalAlignment = HorizontalAlignment.Stretch };
        var browse = new StackPanel
        {
            Spacing = 8,
            Children = { recommended, search, band, ButtonRow(find, refresh, reset), status, results, more }
        };
        content.Content = browse;
        bool working = false;
        int limit = 24;

        async Task Find(bool restart)
        {
            if (working) return;
            if (restart) limit = 24;
            working = true;
            content.IsEnabled = false;
            results.Children.Clear();
            more.Visibility = Visibility.Collapsed;
            status.Text = "Searching Hugging Face…";
            try
            {
                var filter = HuggingFaceModels.Parse(search.Text, band.SelectedIndex);
                var models = await HuggingFaceModels.FindAsync(filter, limit, closing.Token);
                foreach (var model in models)
                {
                    string count = model.Parameters switch
                    {
                        >= 1_000_000_000 => $"{model.Parameters / 1e9:0.##}B parameters",
                        > 0 => $"{model.Parameters / 1e6:0.##}M parameters",
                        _ => "Parameter count unavailable"
                    };
                    string size = model.Files is { Length: > 0 } files
                        ? $" · From {HuggingFaceModels.Size(files[0].Bytes)}" : "";
                    var open = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Content = ToolCardText(model.Id, count + size + " · CPU/GPU candidate")
                    };
                    open.Click += async (_, _) =>
                    {
                        if (working) return;
                        working = true;
                        content.IsEnabled = false;
                        try
                        {
                            var available = model.Files ?? await HuggingFaceModels.FilesAsync(model.Id, closing.Token);
                            content.Content = ModelFiles(model.Id, available, content, browse, busy);
                        }
                        catch (Exception error) { ModelError(status, error); }
                        finally { working = false; content.IsEnabled = true; }
                    };
                    results.Children.Add(open);
                }
                status.Text = models.Length == 0 ? "No matching public single-file models in this batch."
                    : $"{models.Length} results · Compatibility depends on the model file.";
                if (limit < 96 && (filter.FiltersFiles || filter.FiltersParameters || models.Length == limit))
                    more.Visibility = Visibility.Visible;
            }
            catch (Exception error) { ModelError(status, error); }
            finally { working = false; content.IsEnabled = true; }
        }

        void Suggest(string group, params (string Name, string Detail, string Query)[] entries)
        {
            suggestions.Children.Add(ToolText(group));
            foreach (var entry in entries)
            {
                var row = new Button
                {
                    Content = ToolCardText(entry.Name, entry.Detail), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                row.Click += async (_, _) =>
                {
                    search.Text = entry.Query;
                    band.SelectedIndex = 0;
                    await Find(true);
                };
                suggestions.Children.Add(row);
            }
        }
        Suggest("NPU",
            ("LFM2.5 230M", "Verified in Phoenix · Fast conversation", "lfm2.5-230m"),
            ("LFM2.5 350M", "Verified in Phoenix · General chat", "lfm2.5-350m"),
            ("Qwen2.5 0.5B", "Experimental NPU candidate · A Phoenix NPU runner would be needed", "Qwen/Qwen2.5-0.5B-Instruct-GGUF"));
        Suggest("iGPU",
            ("LFM2.5 350M", "350M · General chat", "lfm2.5-350m"),
            ("Qwen2.5-Coder 0.5B", "500M · Coding candidate", "Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF"),
            ("LFM3 1.7B", "1.7B · General chat and reasoning", "lfm3-1.7b"));
        Suggest("dGPU",
            ("LFM3 1.7B", "1.7B · General chat and reasoning", "lfm3-1.7b"),
            ("Qwen 3.5 2B", "2B · Local tools and general chat", "lmstudio-community/Qwen3.5-2B-GGUF"));
        Suggest("Decision and tool use",
            ("D1 Omni 600M", "Phoenix’s dedicated router on CPU · Managed in Settings", "d1-omni-600m"),
            ("FunctionGemma 270M", "270M · Candidate for tool selection", "functiongemma"));

        find.Click += async (_, _) => await Find(true);
        refresh.Click += async (_, _) => await Find(false);
        reset.Click += (_, _) =>
        {
            search.Text = "";
            band.SelectedIndex = 0;
            limit = 24;
            results.Children.Clear();
            more.Visibility = Visibility.Collapsed;
            status.Text = "Filters cleared. Search to browse again.";
        };
        more.Click += async (_, _) => { limit += 24; await Find(false); };
        search.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            await Find(true);
        };
        var view = ModelScroll(content);
        async void LoadCatalog(object sender, RoutedEventArgs args)
        {
            view.Loaded -= LoadCatalog;
            await Find(true);
        }
        view.Loaded += LoadCatalog;
        return view;
    }

    private FrameworkElement ModelFiles(string repo, HuggingFaceModels.ModelFile[] files, ContentControl host,
        FrameworkElement previous, Action<bool> busy, Action? installed = null)
    {
        var back = ToolButton("\uE72B", "Back");
        back.Click += (_, _) => host.Content = previous;
        var panel = new StackPanel { Spacing = 7, Children = { back, ToolText(repo) } };
        if (files.Length == 0) panel.Children.Add(ToolText("No single-file GGUF with a published size was found.", muted: true));
        foreach (var file in files)
        {
            var row = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = ToolCardText(file.Name, HuggingFaceModels.Size(file.Bytes))
            };
            row.Click += (_, _) => host.Content = ReviewModelDownload(file, repo, host, panel, busy, installed);
            panel.Children.Add(row);
        }
        return panel;
    }

    private FrameworkElement ReviewModelDownload(HuggingFaceModels.ModelFile file, string source, ContentControl host,
        FrameworkElement previous, Action<bool> busy, Action? installed = null)
    {
        var download = ToolButton("\uE896", file.Bytes > HuggingFaceModels.LargeFile ? "Approve download over 2 GB" : "Download");
        var back = ToolButton("\uE72B", "Back");
        var cancel = ToolButton("\uE711", "Cancel download");
        cancel.Visibility = Visibility.Collapsed;
        var progress = new ProgressBar { Maximum = file.Bytes, Visibility = Visibility.Collapsed };
        var status = ToolText("Imported models are available for manual chat on CPU or GPU.", muted: true);
        string name = Path.GetFileNameWithoutExtension(file.Name);
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                ToolCard("Review model download", file.Name),
                ToolText($"Source · {source}\nSize · {HuggingFaceModels.Size(file.Bytes)} ({file.Bytes:N0} bytes)"),
                ButtonRow(download, back, cancel), progress, status
            }
        };
        back.Click += (_, _) => host.Content = previous;
        download.IsEnabled = !ModelLibrary.ReadOnly;
        download.Click += async (_, _) =>
        {
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            void Stop(object sender, RoutedEventArgs args) => transfer.Cancel();
            cancel.Click += Stop;
            busy(true);
            download.IsEnabled = back.IsEnabled = false;
            cancel.Visibility = progress.Visibility = Visibility.Visible;
            progress.Value = 0;
            status.Text = "Downloading…";
            try
            {
                var updates = new Progress<long>(bytes => progress.Value = bytes);
                await ModelLibrary.AddAsync(name, source, (stage, token) =>
                    HuggingFaceModels.DownloadAsync(file, stage, updates, token), transfer.Token, sourceFile: file.Name);
                installed?.Invoke();
                status.Text = "Added to Manage Models.";
                download.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                if (transfer.IsCancellationRequested) status.Text = "Download canceled.";
                else ModelError(status, error);
            }
            finally
            {
                cancel.Click -= Stop;
                cancel.Visibility = progress.Visibility = Visibility.Collapsed;
                download.IsEnabled = back.IsEnabled = true;
                busy(false);
            }
        };
        return panel;
    }

    private FrameworkElement BuildModelLibrary(Action<bool> busy)
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var library = new StackPanel { Spacing = 8 };
        var list = new StackPanel { Spacing = 6 };
        var status = ToolText(ModelLibrary.ReadOnly ? "The model library could not be opened for changes." : "", muted: true);
        var local = ToolButton("\uE8E5", "Import file");
        var link = ToolButton("\uE71B", "From link");
        var updates = new Button { Content = "Check for updates" };
        var address = new TextBox { PlaceholderText = "Direct HTTPS link or Hugging Face publisher/model", MaxLength = 4096, Visibility = Visibility.Collapsed };
        var review = ToolButton("\uE721", "Check source");
        review.Visibility = Visibility.Collapsed;
        local.IsEnabled = link.IsEnabled = !ModelLibrary.ReadOnly;
        library.Children.Add(ButtonRow(local, link, updates));
        library.Children.Add(address);
        library.Children.Add(review);
        library.Children.Add(status);
        library.Children.Add(list);
        host.Content = library;

        updates.Click += async (_, _) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var slots = new SemaphoreSlim(4);
            busy(true);
            host.IsEnabled = false;
            status.Text = "Checking publisher versions…";
            try
            {
                var tracked = ModelLibrary.All().SelectMany(model => ModelUpdates.Chat(project, model))
                    .Concat(ModelUpdates.Services(project)).Where(item => item.Check is not null).ToArray();
                var results = await Task.WhenAll(tracked.Select(async item =>
                {
                    try
                    {
                        await slots.WaitAsync(deadline.Token);
                        try { return (item.Key, await item.Check!(deadline.Token)); }
                        finally { slots.Release(); }
                    }
                    catch (Exception error)
                    {
                        DiagnosticLog.Write("models", $"Update check for {item.Key}: {error.GetType().Name}");
                        string detail = error is OperationCanceledException ? "Update check timed out." : "Couldn’t verify the publisher version.";
                        return (item.Key, detail);
                    }
                }));
                if (closing.IsCancellationRequested) return;
                foreach (var (key, result) in results) modelUpdateResults[key] = result;
                Reload();
                status.Text = "Publisher checks finished.";
            }
            catch (Exception error) { ModelError(status, error); }
            finally { busy(false); host.IsEnabled = true; }
        };

        link.Click += (_, _) =>
        {
            address.Visibility = review.Visibility = Visibility.Visible;
            address.Focus(FocusState.Programmatic);
        };
        review.Click += async (_, _) =>
        {
            busy(true);
            host.IsEnabled = false;
            try
            {
                string input = address.Text.Trim();
                if (!Uri.TryCreate(input, UriKind.Absolute, out _))
                    host.Content = ModelFiles(input, await HuggingFaceModels.FilesAsync(input, closing.Token), host, library, busy, Reload);
                else if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                    uri.Host == "huggingface.co" && uri.AbsolutePath.Trim('/').Split('/').Length == 2)
                {
                    string repo = uri.AbsolutePath.Trim('/');
                    host.Content = ModelFiles(repo, await HuggingFaceModels.FilesAsync(repo, closing.Token), host, library, busy, Reload);
                }
                else
                {
                    var file = await HuggingFaceModels.InspectUrlAsync(input, closing.Token);
                    host.Content = ReviewModelDownload(file, file.Address.GetLeftPart(UriPartial.Path), host, library, busy, Reload);
                }
                status.Text = "";
            }
            catch (Exception error) { ModelError(status, error); }
            finally { busy(false); host.IsEnabled = true; }
        };
        local.Click += async (_, _) =>
        {
            busy(true);
            host.IsEnabled = false;
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".gguf");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                if (!HuggingFaceModels.IsSingleModel(file.Name))
                    throw new InvalidOperationException("Choose a single-file GGUF chat model.");
                status.Text = "Copying model into Phoenix…";
                await ModelLibrary.AddAsync(Path.GetFileNameWithoutExtension(file.Name), "Local file", async (stage, cancel) =>
                {
                    await using var input = File.OpenRead(file.Path);
                    await using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                    await input.CopyToAsync(output, cancel);
                }, closing.Token);
                Reload();
                status.Text = "Model imported. The source file stays where it was.";
            }
            catch (Exception error) { ModelError(status, error); }
            finally { busy(false); host.IsEnabled = true; }
        };

        void Reload()
        {
            list.Children.Clear();
            list.Children.Add(ToolText("Voice and routing"));
            foreach (var service in ModelUpdates.Services(project))
                list.Children.Add(new StackPanel { Spacing = 3,
                    Children = { ToolCard(service.Name, service.Installed), UpdateRow(service, includeInstalled: false) } });
            list.Children.Add(ToolText("Chat models"));
            var saved = WorkProfiles.Benchmarks()?.Results;
            foreach (var model in ModelLibrary.All())
            {
                string gguf = LocalEngine.GgufPath(project, model);
                bool builtIn = ModelLibrary.IsBuiltIn(model);
                bool npuEngine = LocalEngine.SupportsNpu(model);
                string? npu = npuEngine ? LocalEngine.WeightPath(project, model, LocalBackend.Npu) : null;
                var paths = new[] { gguf, npu }.OfType<string>().Where(File.Exists).ToArray();
                long bytes = paths.Sum(path => new FileInfo(path).Length);
                bool visible = ModelLibrary.IsVisible(model);
                if (!builtIn && !visible && bytes == 0) continue;
                bool selected = selectedModel == model;
                bool verified = npuEngine && (model == LocalModel.Lfm230 ? npuAvailable : LocalEngine.IsVerified(project, model));
                string source = ModelLibrary.Find(model)?.Source ?? (model == LocalModel.Gemma
                    ? "Optional · Select manually to use" : model == LocalModel.Qwen35 ? "Recommended for local tools" : "Built in");
                var measured = saved?.Where(row => row.Model == model && row.DecodeRate > 0).MaxBy(row => row.DecodeRate);
                string detail = (selected ? "Selected" : !visible ? "Hidden" : "In model picker") + " · " +
                    (bytes > 0 ? HuggingFaceModels.Size(bytes) : "Model file missing") +
                    (verified ? " · Verified NPU" : " · CPU/GPU") +
                    (measured is null || bytes <= 0 ? "" : $" · {measured.DecodeRate:0.#} tok/s on {measured.Device}");
                var choose = ToolButton("\uE73E", "Use model");
                choose.IsEnabled = (!selected || desktopWorkMode != WorkMode.Manual) && visible && ModelAvailable(model);
                var hide = ToolButton("\uED1A", visible ? "Hide" : "Show");
                hide.IsEnabled = !selected && !ModelLibrary.ReadOnly;
                var delete = ToolButton("\uE74D", "Delete files");
                // The bundled NPU weights also serve the permanent gatekeeper and voice bridge.
                string[] deletable = model == LocalModel.Lfm230 ? paths.Where(path => path == gguf).ToArray() : paths;
                delete.IsEnabled = !selected && deletable.Length > 0 && !ModelLibrary.ReadOnly;
                var card = new StackPanel { Spacing = 5, Children =
                    { ToolCard(ModelName(model), $"{detail}\n{source}") } };
                foreach (var variant in ModelUpdates.Chat(project, model)) card.Children.Add(UpdateRow(variant));
                card.Children.Add(ButtonRow(choose, hide, delete));
                list.Children.Add(card);
                choose.Click += (_, _) =>
                {
                    StopSelectedModel();
                    ReleaseWarmWorker();
                    desktopWorkMode = WorkMode.Manual;
                    desktopBackend = LocalBackend.Auto;
                    desktopThinking = false;
                    SetModelSelection(model);
                    RouteLocal();
                    Reload();
                };
                hide.Click += (_, _) =>
                {
                    try
                    {
                        ModelLibrary.SetVisible(model, !visible);
                        ReleaseWarmWorker();
                        Reload();
                    }
                    catch (Exception error) { ModelError(status, error); }
                };
                delete.Click += (_, _) =>
                {
                    var back = ToolButton("\uE72B", "Cancel");
                    var confirm = ToolButton("\uE74D", "Delete these files");
                    host.Content = new StackPanel { Spacing = 8, Children =
                    {
                        ToolCard("Delete model files", ModelName(model)),
                        ToolText(string.Join("\n", deletable.Select(path => $"{Path.GetFileName(path)} · {HuggingFaceModels.Size(new FileInfo(path).Length)}"))),
                        ButtonRow(confirm, back)
                    } };
                    back.Click += (_, _) => host.Content = library;
                    confirm.Click += async (_, _) =>
                    {
                        busy(true);
                        confirm.IsEnabled = back.IsEnabled = false;
                        try
                        {
                            foreach (string path in deletable) ModelLibrary.CheckManagedPath(path);
                            ReleaseWarmWorker();
                            await WaitForRetiringWorkersAsync(closing.Token);
                            ModelLibrary.SetVisible(model, false);
                            foreach (string path in deletable) File.Delete(path);
                            status.Text = "Model files deleted.";
                        }
                        catch (Exception error) { ModelError(status, error); }
                        finally { busy(false); Reload(); host.Content = library; }
                    };
                };
            }
        }

        FrameworkElement UpdateRow(ModelUpdates.Track track, bool includeInstalled = true)
        {
            string label = includeInstalled ? $"{track.Name} · {track.Installed}" : "";
            string? result = track.Check is not null ? modelUpdateResults.GetValueOrDefault(track.Key) ?? "Version unchecked" :
                track.Publisher is null ? "Publisher untracked" : null;
            var row = new StackPanel { Spacing = 2 };
            if (label.Length > 0 || result is not null)
                row.Children.Add(ToolText(string.Join(" · ", new[] { label, result }.Where(value => !string.IsNullOrEmpty(value))), muted: true));
            if (track.Publisher is { } publisher)
                row.Children.Add(new HyperlinkButton { Content = includeInstalled ? $"{track.Name} publisher" : "Publisher",
                    NavigateUri = publisher, FontSize = 12, Padding = new Thickness(0, 0, 0, 1), MinHeight = 20 });
            return row;
        }
        Reload();
        return ModelScroll(host);
    }

    private void ModelError(TextBlock status, Exception error)
    {
        DiagnosticLog.Write("models", error.GetType().Name);
        status.Text = error switch
        {
            System.Net.Http.HttpRequestException { StatusCode: { } code } => $"The model source returned HTTP {(int)code}.",
            System.Net.Http.HttpRequestException => "The model source could not be reached.",
            OperationCanceledException => "The request timed out or was canceled.",
            _ => error.Message
        };
    }
}
