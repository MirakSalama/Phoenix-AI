using System.Text;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NpuChat;

public sealed partial class MainWindow
{
    private async void Workspace_Click(object sender, RoutedEventArgs args)
    {
        if (settingsOpen || phase is not (Phase.Ready or Phase.Failed)) return;
        settingsOpen = true;
        try
        {
            if (workspaceUsable) await ShowToolsAsync();
            else await WorkspaceDialog("Models & Tools", Wrapped("The local workspace couldn’t be opened. Check the diagnostic logs.")).ShowAsync();
        }
        catch (Exception error)
        {
            DiagnosticLog.Write("workspace", $"Tools window: {error.GetType().Name}");
            FullStatus.Text = "The Tools window couldn’t be opened.";
        }
        finally
        {
            settingsOpen = false;
            WorkspaceButton.Focus(FocusState.Programmatic);
        }
    }

    private async Task ShowToolsAsync()
    {
        var size = Surface.XamlRoot.Size;
        double width = Math.Clamp(size.Width - 80, 264, 740);
        double height = Math.Clamp(size.Height - 188, 190, 495);
        var root = new Grid { ColumnSpacing = 14 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width < 460 ? 103 : 143) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var nav = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(2, 4, 2, 4) };
        AutomationProperties.SetName(nav, "Tool categories");
        var rail = new Border { Background = CellFill, CornerRadius = new CornerRadius(8), Child = nav };
        var body = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(body, 1);
        root.Children.Add(rail);
        root.Children.Add(body);

        var catalog = new PivotItem { Header = "Get New Models", Margin = new Thickness(0) };
        var library = new PivotItem { Header = "Manage Models", Margin = new Thickness(0) };
        var tabs = new Pivot { Width = width, Height = height, Items =
        {
            catalog, library, new PivotItem { Header = "Tools", Content = root, Margin = new Thickness(0) }
        } };
        var dialog = WorkspaceDialog("Models & Tools", tabs);
        dialog.Resources["ContentDialogMaxWidth"] = width + 60;
        dialog.Resources["ContentDialogMaxHeight"] = size.Height - 8;
        bool busy = false;
        void SetBusy(bool running)
        {
            busy = running;
            nav.IsEnabled = !running;
            tabs.IsLocked = running;
        }
        dialog.Closing += (_, eventArgs) => { if (busy) eventArgs.Cancel = true; };
        (string Title, string Icon, Func<FrameworkElement> Make)[] sections =
        [
            ("MCP servers", "\uE968", () => BuildMcpTools(SetBusy)),
            ("Skills", "\uE8F1", () => BuildSkillTools(SetBusy)),
            ("Knowledge", "\uE8A5", () => BuildKnowledgeTools(SetBusy)),
            ("Memory", "\uE70B", () => BuildMemoryTools(SetBusy)),
            ("Imports", "\uE896", () => BuildRepositoryTools(SetBusy)),
            ("Plugins", "\uE74C", BuildPluginTools),
            ("Built-in tools", "\uE821", BuildBuiltInTools)
        ];
        foreach (var section in sections)
        {
            var icon = new FontIcon { Glyph = section.Icon, FontSize = 14, Foreground = ToolBrush("Muted") };
            var text = new TextBlock { Text = section.Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var entry = new ListViewItem { Padding = new Thickness(6, 5, 3, 5),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon, text } } };
            AutomationProperties.SetName(entry, section.Title == "Knowledge" ? "Knowledge (RAG)" : section.Title);
            nav.Items.Add(entry);
        }
        var pages = new FrameworkElement?[sections.Length];
        nav.SelectionChanged += (_, _) =>
        {
            int i = nav.SelectedIndex;
            if (i >= 0) body.Content = sections[i].Title is "Imports" or "Skills" or "MCP servers" or "Knowledge" or "Plugins"
                ? sections[i].Make() : pages[i] ??= sections[i].Make();
        };
        nav.SelectedIndex = 0;
        catalog.Content = BuildModelCatalog(SetBusy);
        library.Content = BuildModelLibrary(SetBusy);
        tabs.SelectionChanged += (_, _) =>
        {
            if (!busy && ReferenceEquals(tabs.SelectedItem, library)) library.Content = BuildModelLibrary(SetBusy);
        };
        await dialog.ShowAsync();
    }

    private FrameworkElement BuildMemoryTools(Action<bool> busy)
    {
        var learn = new CheckBox { Content = "Learn from my messages", IsChecked = workspace.LearnFromMessages };
        learn.Click += (_, _) =>
        {
            bool previous = workspace.LearnFromMessages;
            workspace.LearnFromMessages = learn.IsChecked == true;
            if (SaveWorkspace()) return;
            workspace.LearnFromMessages = previous;
            learn.IsChecked = previous;
            FullStatus.Text = "The memory preference couldn’t be saved.";
        };
        return ToolCollection("Memory", "Notes Phoenix can recall across chats.", "Add note",
            workspace.Memory, note => (LocalWorkspace.Limit(note.Text, 120), note.Learned ? "Learned from chat" : "Saved note"),
            [Field("Memory note", 350, multiline: true)], note => [note.Text],
            (old, fields) => new MemoryNote(old?.Id ?? Guid.NewGuid(), Required(fields[0], "A note"), false), busy,
            footer: panel => panel.Children.Add(learn));
    }

    private FrameworkElement BuildSkillTools(Action<bool> busy) =>
        ToolCollection("Skills", "Reusable instructions and workflows.", "Add skill",
            workspace.Skills, skill => (skill.Name, skill.Origin is { } origin ?
                $"Ready · {origin.Repository} · {origin.Commit[..12]}\n{skill.Description}" : skill.Kind == WorkspaceEntryKind.Tool ? "Saved workflow" :
                skill.PublicQuery is not null ? "Skill with web search" : "Skill"),
            [Field("Name", 64), Field("Trigger phrases", 200), Field("Instructions", 256 * 1024, multiline: true)],
            skill => [skill.Name, skill.Triggers, skill.Instructions],
            (old, fields) => new LocalSkill(old?.Id ?? Guid.NewGuid(),
                UniqueName(fields[0], workspace.Skills.Where(skill => skill != old).Select(skill => skill.Name)),
                fields[1], Required(fields[2], "Instructions"), old?.Kind ?? WorkspaceEntryKind.Skill,
                old?.Origin is null ? WorkspaceRequest.SearchQuery(fields[2]) : null,
                Description: old?.Description, Origin: old?.Origin, SkillFile: old?.SkillFile), busy,
                include: skill => skill.Kind != WorkspaceEntryKind.McpDraft,
                editable: skill => skill.Origin is null && skill.SkillFile is null,
                inspect: (form, skill) =>
                {
                    if (skill.SkillFile is { } path)
                        form.Children.Add(new TextBox { Header = "SKILL.md path", Text = path, IsReadOnly = true });
                    else if (skill.Origin is not null)
                        form.Children.Add(ToolText("See the original skill under Tools → Imports.", muted: true));
                });

    private FrameworkElement BuildKnowledgeTools(Action<bool> busy) =>
        ToolCollection("Knowledge (RAG)", "Local documents Phoenix can search for relevant passages.", "Add document",
            workspace.Knowledge, file => (file.Name, $"{file.Text.Length:N0} characters"),
            [Field("Document title", 100), Field("Document text", LocalWorkspace.MaxDocumentBytes, multiline: true)],
            file => [file.Name, file.Text],
            (old, fields) =>
            {
                if (old is null && workspace.Knowledge.Count >= LocalWorkspace.MaxDocuments)
                    throw new InvalidOperationException($"The collection already has {LocalWorkspace.MaxDocuments} documents.");
                string title = UniqueName(fields[0], workspace.Knowledge.Where(file => file != old).Select(file => file.Name));
                string text = Required(fields[1], "Document text");
                if (Encoding.UTF8.GetByteCount(text) > LocalWorkspace.MaxDocumentBytes)
                    throw new InvalidOperationException("Document text must be 2 MB or smaller.");
                return new KnowledgeFile(old?.Id ?? Guid.NewGuid(), title, text);
            }, busy, invalidate: workspace.ForgetDocumentIndex, import: ImportToolDocumentsAsync);

    private FrameworkElement BuildMcpTools(Action<bool> busy)
    {
        CheckBox? reviewedCode = null;
        string? reviewedFingerprint = null;
        var host = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        void ShowServers() => host.Content = ToolCollection("MCP servers", "Registered servers and the profiles that can use them.", "Add server",
            workspace.Servers, server => (server.Name, McpToolSummary(server)),
            [Field("Name", 50), Field("Full path to executable", 260), Field("Arguments (JSON string array)", 2000, multiline: true),
                Field("Trigger phrases", 200), Field("LAN access (private, shared, or profile)", 8)],
            server => [server.Name, server.Command, JsonSerializer.Serialize(server.Arguments), server.Triggers,
                server.ShareWithLan ? "shared" : server.LanProfileKey is not null ? "profile" : "private"],
            (old, fields) => BuildMcpEntry(old, fields, reviewedCode?.IsChecked == true ? reviewedFingerprint : null), busy, test: async server =>
            {
                if (!server.Enabled) return "Review and save this server to enable it before testing.";
                var tools = await McpClient.ListAsync(server, closing.Token);
                return tools.Count == 0 ? "The server responded without tools." :
                    "Discovered " + string.Join(", ", tools.Take(12).Select(tool => tool.Name)) + ".";
            }, example: CreateMcpStarter, inspect: (form, server) =>
            {
                reviewedCode = null;
                reviewedFingerprint = null;
                if (server.Origin is { } origin)
                {
                    form.Children.Add(ToolText($"Source: {origin.Repository} · {origin.Commit[..12]} · {origin.Path}", muted: true));
                    if (server.Command.Equals("npx", StringComparison.OrdinalIgnoreCase))
                        form.Children.Add(ToolText("Running this server may fetch the npm package shown in Arguments.", muted: true));
                    if (server.WorkingDirectory is { } folder && server.Arguments.FirstOrDefault(arg => File.Exists(arg) &&
                        Path.GetDirectoryName(arg) == folder) is { } importedSource)
                        form.Children.Add(new TextBox { Header = "Imported MCP entry source", Text = File.ReadAllText(importedSource),
                            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 });
                }
                if (server.GeneratedModule is not { } source) return;
                string sourceText = File.ReadAllText(source);
                string schemaText = File.ReadAllText(server.Arguments[^2]);
                var code = new TextBox { Header = "Generated Python source", Text = sourceText,
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };
                var schema = new TextBox { Header = "Tool input schema", Text = schemaText,
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                reviewedFingerprint = ToolAuthoring.Fingerprint(sourceText, schemaText);
                reviewedCode = new CheckBox { Content = "I reviewed this source. Enable the tool.",
                    IsChecked = server.Enabled && server.ReviewedFingerprint == reviewedFingerprint };
                form.Children.Add(code);
                form.Children.Add(schema);
                form.Children.Add(reviewedCode);
            }, footer: panel =>
            {
                int drafts = PendingMcpDrafts().Count();
                if (drafts == 0) return;
                var button = ToolButton("\uE8A5", $"Review drafts ({drafts})");
                button.Click += (_, _) => ShowDrafts();
                panel.Children.Add(button);
            });

        void ShowDrafts() => host.Content = ToolCollection<LocalSkill>("MCP drafts",
            "Drafts awaiting registration or activation.", null, workspace.Skills,
            skill => (skill.Name, "Desktop draft"),
            [Field("Name", 50), Field("Purpose", 480, multiline: true)],
            skill => [skill.Name, skill.Instructions],
            (old, fields) => old! with
            {
                Name = UniqueName(fields[0], workspace.Skills.Where(skill => skill != old).Select(skill => skill.Name)),
                Instructions = Required(fields[1], "A purpose")
            }, busy, listBack: ShowServers, include: skill => skill.Kind == WorkspaceEntryKind.McpDraft &&
                !workspace.Servers.Any(server => server.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)),
            footer: panel =>
            {
                foreach (var (_, draft) in PendingMcpDrafts().Where(item => item.ProfileKey is not null))
                    panel.Children.Add(new Expander { Header = draft.Name + " · Submitted from LAN",
                        Content = Wrapped(draft.Instructions), HorizontalAlignment = HorizontalAlignment.Stretch });
                panel.Children.Add(ToolText("To review a template for activation, say “activate MCP” followed by its name in chat.", muted: true));
            });
        ShowServers();
        return host;
    }

    private McpServer BuildMcpEntry(McpServer? old, string[] fields, string? reviewedFingerprint = null)
    {
        if (old?.RemoteUrl is not null)
            throw new InvalidOperationException("This remote MCP server is recorded in Imports. Remote connections are not enabled in this build.");
        string path = Required(fields[1], "An executable path").Trim('"');
        string[] arguments = ReadMcpArguments(fields[2]);
        if (!Path.IsPathFullyQualified(path) && old is { Enabled: false, Origin: not null } && !old.NeedsSetup)
        {
            if (path.Equals("npx", StringComparison.OrdinalIgnoreCase) && ResolveImportedExecutable("node") is { } node)
            {
                string npx = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npx-cli.js");
                if (File.Exists(npx))
                {
                    path = node;
                    arguments = [npx, .. arguments];
                }
            }
            else if (path is "python" or "python3" && LocalEngine.LocalPython is { } python && File.Exists(python))
                path = python;
            else path = ResolveImportedExecutable(path) ?? path;
        }
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter an existing .exe file by its full path.");
        if (old?.GeneratedModule is not null && !SameMcpCommand(old, path, arguments))
            throw new InvalidOperationException("A generated draft keeps its reviewed command and arguments.");
        string access = fields[4].ToLowerInvariant();
        if (access is not ("" or "private" or "shared") && (access != "profile" || old?.LanProfileKey is null))
            throw new InvalidOperationException("Enter private or shared. Profile access is available for a submitted draft.");
        return new McpServer(old?.Id ?? Guid.NewGuid(),
            UniqueName(fields[0], workspace.Servers.Where(server => server != old).Select(server => server.Name)),
            path, arguments, fields[3], access == "shared", access == "profile" ? old?.LanProfileKey : null,
            old is { ApprovedClock: true } && SameMcpCommand(old, path, arguments),
            old is { ApprovedChrome: true } && SameMcpCommand(old, path, arguments),
            Enabled: old?.GeneratedModule is null || reviewedFingerprint is not null, Origin: old?.Origin,
            GeneratedModule: old?.GeneratedModule, ReviewedFingerprint: reviewedFingerprint, WorkingDirectory: old?.WorkingDirectory,
            Description: old?.Description);
    }

    private static string? ResolveImportedExecutable(string command)
    {
        if (command.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || command is "." or "..") return null;
        string name = command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? command : command + ".exe";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
    }

    private string McpToolSummary(McpServer server)
    {
        string scope = server.ShareWithLan ? "Paired LAN devices" : server.LanProfileKey is not null ? "One LAN profile" : "Desktop";
        if (!server.Enabled && server.GeneratedModule is not null) return $"Generated draft · Review source to enable · {scope}";
        if (!server.Enabled)
            return (server.RemoteUrl is not null ? "Remote endpoint · Needs setup" :
                server.NeedsSetup ? "Needs setup · Review command and arguments" : "Disabled · Review and save to enable") +
                (server.Origin is { } origin ? $"\n{origin.Repository} · {origin.Commit[..12]}" : "");
        if (!File.Exists(server.Command) || (server.ApprovedChrome && (!IsApprovedChrome(server) || !File.Exists(ChromeScript))) ||
            (server.ApprovedClock && (!IsApprovedClock(server) || !File.Exists(ClockScript))))
            return $"Needs repair · {scope}";
        return (server.ApprovedChrome ? "Public Chrome pages" : server.ApprovedClock ? "Local clock" : "Registered") + " · " + scope;
    }

    private FrameworkElement BuildBuiltInTools()
    {
        string web = string.IsNullOrWhiteSpace(cloudKey) ? "API key needed in Settings" :
            $"{CloudResearch.Name(researchProvider)} · On request";
        var panel = new StackPanel { Spacing = 9, Children =
        {
            new TextBlock { Text = "Built-in tools", FontSize = 19, FontWeight = FontWeights.SemiBold },
            ToolCard("Web search", web),
            ToolCard("Weather", "Daily forecasts from Open-Meteo"),
            ToolCard("Currency rates", "Daily reference rates"),
            ToolCard("Private memory", "Save and recall notes"),
            ToolCard("Document search", "Search your Knowledge collection")
        }};
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private FrameworkElement BuildPluginTools()
    {
        var panel = new StackPanel { Spacing = 9, Children = { ToolText("Imported plugin declarations") } };
        foreach (var item in workspace.Imports)
            foreach (var plugin in item.Findings.Where(finding => finding.Kind == "Plugin"))
                panel.Children.Add(ToolCard(plugin.Name, $"{plugin.Status}\n{item.Repository} · {item.Commit[..12]}"));
        if (panel.Children.Count == 1) panel.Children.Add(ToolText("No plugins found in imported repositories.", muted: true));
        return new ScrollViewer { Content = panel };
    }

    private FrameworkElement ToolCollection<T>(string title, string help, string? addLabel,
        List<T> items, Func<T, (string Title, string Detail)> describe,
        TextBox[] fields, Func<T, string[]> read, Func<T?, string[], T> build, Action<bool> busy,
        Func<T, bool>? include = null, Action<StackPanel>? footer = null, Action? listBack = null,
        Func<T, Task<string>>? test = null, Func<T>? example = null,
        Action<T>? invalidate = null, Func<Task<string>>? import = null, Action<StackPanel, T>? inspect = null,
        Func<T, bool>? editable = null) where T : class
    {
        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        var action = new ContentControl();
        Grid.SetColumn(action, 1);
        header.Children.Add(caption);
        header.Children.Add(action);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var formActions = new ContentControl();
        var status = ToolText("", muted: true);
        status.MaxLines = 4;
        Grid.SetRow(formActions, 1);
        Grid.SetRow(status, 2);
        Grid.SetRow(scroll, 3);
        root.Children.Add(header);
        root.Children.Add(scroll);
        root.Children.Add(formActions);
        root.Children.Add(status);

        void ShowList(string message = "")
        {
            caption.Text = title;
            action.Content = null;
            formActions.Content = null;
            formActions.Visibility = Visibility.Collapsed;
            status.Text = message;
            if (addLabel is not null)
            {
                var add = ToolButton("\uE710", addLabel);
                add.Click += (_, _) => ShowForm(null);
                action.Content = add;
            }
            var list = new StackPanel { Spacing = 7, Children = { ToolText(help, muted: true) } };
            if (listBack is not null)
            {
                var back = ToolButton("\uE72B", "Back to servers");
                back.Click += (_, _) => listBack();
                list.Children.Add(back);
            }
            bool any = false;
            foreach (var item in items.Where(item => include?.Invoke(item) ?? true))
            {
                any = true;
                var (name, detail) = describe(item);
                var row = new Button { Content = ToolCardText(name, detail), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7) };
                AutomationProperties.SetName(row, $"{name}, {detail}");
                row.Click += (_, _) => ShowForm(item);
                list.Children.Add(row);
            }
            if (!any && listBack is null) list.Children.Add(ToolText("Nothing saved here yet.", muted: true));
            footer?.Invoke(list);
            scroll.Content = list;
            scroll.ChangeView(null, 0, null);
        }

        void ShowForm(T? current)
        {
            bool canEdit = current is null || editable?.Invoke(current) != false;
            caption.Text = current is null ? addLabel : $"{(canEdit ? "Edit" : "View")} {describe(current).Title}";
            status.Text = "";
            var back = ToolButton("\uE72B", "Back");
            back.Click += (_, _) => ShowList();
            action.Content = back;
            var form = new StackPanel { Spacing = 7 };
            var values = current is null ? null : read(current);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Parent is Panel previous) previous.Children.Remove(fields[i]);
                fields[i].Text = values?[i] ?? "";
                fields[i].IsReadOnly = !canEdit;
                form.Children.Add(fields[i]);
            }
            if (current is not null && inspect is not null)
            {
                try { inspect(form, current); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
                { form.Children.Add(ToolText("The source or schema could not be opened.", muted: true)); }
            }
            var save = ToolButton("\uE74E", "Save");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (canEdit) buttons.Children.Add(save);
            formActions.Content = buttons;
            formActions.Visibility = Visibility.Visible;
            save.Click += (_, _) =>
            {
                try
                {
                    int index = current is null ? items.Count : items.IndexOf(current);
                    T entry = build(current, fields.Select(field => field.Text.Trim()).ToArray());
                    if (current is null) items.Add(entry);
                    else items[index] = entry;
                    if (!SaveWorkspace())
                    {
                        if (current is null) items.RemoveAt(index);
                        else items[index] = current;
                        throw new IOException("The entry couldn’t be saved.");
                    }
                    if (current is not null) invalidate?.Invoke(current);
                    ShowList("Saved.");
                }
                catch (Exception error) { status.Text = error.Message; }
            };
            if (current is not null)
            {
                var remove = IconAction("\uE74D", "Delete entry");
                remove.Click += (_, _) =>
                {
                    int index = items.IndexOf(current);
                    items.RemoveAt(index);
                    if (!SaveWorkspace())
                    {
                        items.Insert(index, current);
                        status.Text = "The entry couldn’t be removed.";
                        return;
                    }
                    invalidate?.Invoke(current);
                    ShowList("Removed.");
                };
                buttons.Children.Add(remove);
                if (test is not null)
                {
                    var verify = IconAction("\uE721", "Discover tools");
                    verify.Click += async (_, _) => await RunAsync("Discovering tools…", () => test(current), returnToList: false);
                    buttons.Children.Add(verify);
                }
            }
            else if (example is not null)
            {
                var starter = IconAction("\uE8A5", "Create clock example");
                starter.Click += (_, _) =>
                {
                    try { ShowList($"Added {describe(example()).Title}. Open it to discover its tools."); }
                    catch (Exception error) { status.Text = error.Message; }
                };
                buttons.Children.Add(starter);
            }
            if (current is null && import is not null)
            {
                var files = IconAction("\uE8E5", "Import files");
                files.Click += async (_, _) => await RunAsync("Opening file picker…", import, returnToList: true);
                buttons.Children.Add(files);
            }
            scroll.Content = form;
            scroll.ChangeView(null, 0, null);
            fields[0].Focus(FocusState.Programmatic);
        }

        async Task RunAsync(string working, Func<Task<string>> run, bool returnToList)
        {
            busy(true);
            action.IsEnabled = scroll.IsEnabled = formActions.IsEnabled = false;
            status.Text = working;
            try
            {
                string message = await run();
                if (returnToList) ShowList(message);
                else status.Text = message;
            }
            catch (OperationCanceledException) { status.Text = "The operation was cancelled or timed out."; }
            catch (Exception error) { status.Text = error.Message; }
            finally
            {
                action.IsEnabled = scroll.IsEnabled = formActions.IsEnabled = true;
                busy(false);
            }
        }
        ShowList();
        return root;
    }

    private async Task<string> ImportToolDocumentsAsync()
    {
        int before = workspace.Knowledge.Count;
        int skipped = 0;
        try
        {
            var picker = new FileOpenPicker();
            foreach (string type in new[] { ".txt", ".md", ".markdown", ".csv", ".json" }) picker.FileTypeFilter.Add(type);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            foreach (var file in await picker.PickMultipleFilesAsync())
            {
                var properties = await file.GetBasicPropertiesAsync();
                if (workspace.Knowledge.Count >= LocalWorkspace.MaxDocuments || properties.Size > LocalWorkspace.MaxDocumentBytes)
                {
                    skipped++;
                    continue;
                }
                string text = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > LocalWorkspace.MaxDocumentBytes ||
                    workspace.Knowledge.Any(document => document.Name == file.Name && document.Text == text))
                {
                    skipped++;
                    continue;
                }
                workspace.Knowledge.Add(new KnowledgeFile(Guid.NewGuid(), file.Name, text));
            }
            int added = workspace.Knowledge.Count - before;
            if (added > 0 && !SaveWorkspace()) throw new IOException("The documents couldn’t be saved.");
            return added == 0 && skipped == 0 ? "" : $"Imported {added}. Skipped {skipped}.";
        }
        catch
        {
            workspace.Knowledge.RemoveRange(before, workspace.Knowledge.Count - before);
            throw;
        }
    }

    private Brush ToolBrush(string name) => (Brush)Surface.Resources[name];

    private TextBlock ToolText(string text, bool muted = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = muted ? 12 : 14,
        Foreground = muted ? ToolBrush("Muted") : Light
    };

    private StackPanel ToolCardText(string title, string detail)
    {
        var label = ToolText(title);
        label.FontWeight = FontWeights.SemiBold;
        return new StackPanel { Spacing = 3, Children = { label, ToolText(detail, muted: true) } };
    }

    private Border ToolCard(string title, string detail) => new()
    {
        Background = ToolBrush("Panel"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 7, 10, 7),
        Child = ToolCardText(title, detail)
    };

    private Button ToolButton(string glyph, string title)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 13, Foreground = ToolBrush("Mint") };
        var label = new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Padding = new Thickness(8, 5, 8, 5),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon, label } } };
        SetActionName(button, title);
        return button;
    }
}
