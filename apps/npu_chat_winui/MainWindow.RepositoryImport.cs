using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NpuChat;

public sealed partial class MainWindow
{
    private RepoScan? scannedRepository;

    private async Task DiscoverRepositoriesAsync(RepoDiscoveryRequest request)
    {
        SetReplySource("PHOENIX");
        SetPhase(Phase.PreparingTools, "Searching GitHub for existing tools…");
        AnnounceWork("I’ll look for an existing project.");
        try
        {
            var results = await new RepositoryImport().SearchAsync(request, closing.Token);
            if (closing.IsCancellationRequested) return;
            if (results.Length == 0)
            {
                FinishReply("I didn’t find a GitHub repository for those terms. Try the integration’s name or paste a repository link.");
                return;
            }
            string[] urls = results.Select(result => $"https://github.com/{result.Repository}").ToArray();
            var sources = results.Select((result, index) => new Citation(result.Repository, new Uri(urls[index]))).ToArray();
            var rows = results.Select((result, index) =>
                $"{index + 1}. {result.Repository} ({result.Stars:N0} stars)" +
                (result.Description.Length == 0 ? "" : $"\n   {result.Description}"));
            Record(new("GitHub", "Search completed", $"{results.Length} repository matches"));
            FinishReply($"I found these GitHub repositories. Ask me to scan or install one by number.\n\n{string.Join("\n\n", rows)}",
                sources, repositoryOptions: urls);
        }
        catch (Exception error) when (!closing.IsCancellationRequested)
        {
            DiagnosticLog.Write("repository-import", $"Search failed: {error.GetType().Name}");
            FinishReply(error is InvalidOperationException ? error.Message : "I couldn’t search GitHub right now.");
        }
    }

    private async Task RunRepositoryImportAsync(RepoImportRequest request)
    {
        SetReplySource("PHOENIX");
        SetPhase(Phase.PreparingTools, "Scanning GitHub for skills and tools…");
        AnnounceWork("I’ll check the repository for installable files.");
        try
        {
            var scan = await new RepositoryImport().ScanAsync(request.Url, closing.Token);
            if (closing.IsCancellationRequested) return;
            Record(new("GitHub", "Scan completed", $"{scan.Repository} · {scan.Commit[..12]}"));
            if (activeLanProfile is null) scannedRepository = scan;
            string[] options = [$"https://github.com/{scan.Repository}/commit/{scan.Commit}"];
            if (!request.Install || scan.Findings.Count == 0)
            {
                FinishReply(RepositoryPreview(scan) + (scan.Findings.Count > 0 ? "\n\nSay “install it” to review this commit." : ""),
                    repositoryOptions: scan.Findings.Count > 0 ? options : null);
                return;
            }
            if (activeLanProfile is not null)
            {
                FinishReply(RepositoryPreview(scan) + "\n\nOpen Tools → Imports on the Phoenix PC to review and install it.");
                return;
            }
            var dialog = WorkspaceDialog("Import from GitHub", new ScrollViewer
            {
                MaxHeight = 350, Content = Wrapped(RepositoryPreview(scan) +
                    "\n\nSkills and reference files will be saved. Review and enable imported MCP servers in Tools before running them.")
            });
            dialog.PrimaryButtonText = "Import";
            dialog.CloseButtonText = "Cancel";
            dialog.DefaultButton = ContentDialogButton.Close;
            RestoreFromTray();
            if (compact) SetCompact(false);
            settingsOpen = true;
            ContentDialogResult decision;
            try { decision = await dialog.ShowAsync(); }
            finally { settingsOpen = false; }
            if (closing.IsCancellationRequested) return;
            if (decision == ContentDialogResult.Primary)
            {
                string result = InstallRepository(scan);
                Record(new("Repository import", "Saved", scan.Repository));
                FinishReply(result);
            }
            else FinishReply("The repository scan is ready in Tools → Imports.", repositoryOptions: options);
        }
        catch (Exception error) when (!closing.IsCancellationRequested)
        {
            DiagnosticLog.Write("repository-import", $"Scan or install failed: {error.GetType().Name}");
            FinishReply(error is InvalidOperationException ? error.Message :
                error is OperationCanceledException ? "The GitHub scan timed out." : "I couldn’t read or save that repository.");
        }
    }

    private string InstallRepository(RepoScan scan) => RepositoryInstallation.Save(workspace, scan,
        Path.Combine(LocalWorkspace.Folder, "Imports"), SaveWorkspace);

    private static string RepositoryPreview(RepoScan scan)
    {
        string revision = $"{scan.Repository} at {scan.Commit[..12]}";
        if (scan.Findings.Count == 0) return $"I found no supported skills, MCP configs, instructions, or plugins in {revision}.";
        var rows = scan.Findings.Take(18).Select(item => $"- {item.Kind}: {item.Name} · {item.Status}");
        return $"Found {scan.Findings.Count} items in {revision}.\n\n{string.Join("\n", rows)}" +
            (scan.Findings.Count > 18 ? "\n\nOpen Tools → Imports to see the full list." : "");
    }

    private FrameworkElement BuildRepositoryTools(Action<bool> busy)
    {
        var address = Field("GitHub repository URL", 400);
        var scanButton = new Button { Content = "Scan" };
        var importButton = new Button { Content = "Import", IsEnabled = scannedRepository is { Findings.Count: > 0 } };
        var status = ToolText(scannedRepository is null ? "" : $"{scannedRepository.Repository} · {scannedRepository.Commit[..12]}", muted: true);
        var preview = new StackPanel { Spacing = 5 };
        var installed = new StackPanel { Spacing = 8 };
        var panel = new StackPanel
        {
            Spacing = 8,
            Children = { ToolText("GitHub imports"), address, ButtonRow(scanButton, importButton), status, preview, installed }
        };

        void ShowPreview()
        {
            preview.Children.Clear();
            if (scannedRepository is not { } current) return;
            foreach (var entry in current.Findings)
                preview.Children.Add(ToolText($"{entry.Kind} · {entry.Name}\n{entry.Status} · {entry.Path}", muted: true));
        }

        void ShowInstalled()
        {
            installed.Children.Clear();
            foreach (var item in workspace.Imports.AsEnumerable().Reverse())
            {
                var entries = new StackPanel { Spacing = 6 };
                foreach (var entry in item.Findings)
                {
                    entries.Children.Add(ToolText($"{entry.Kind} · {entry.Name}\n{entry.Status} · {entry.Path}", muted: true));
                    if (entry.Kind is not ("Instructions" or "Skill")) continue;
                    var open = new Button { Content = $"View {entry.Name}" };
                    var add = new Button { Content = "Add to Knowledge" };
                    var content = ToolText("", muted: true);
                    content.IsTextSelectionEnabled = true;
                    open.Click += (_, _) =>
                    {
                        try { content.Text = RepositoryInstallation.ReadText(Path.Combine(LocalWorkspace.Folder, "Imports"), item.Id, entry.Path); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
                        { content.Text = "This reference could not be opened."; }
                    };
                    add.Click += (_, _) =>
                    {
                        try
                        {
                            string reference = RepositoryInstallation.ReadText(Path.Combine(LocalWorkspace.Folder, "Imports"), item.Id, entry.Path);
                            string title = LocalWorkspace.Limit($"{item.Repository} · {entry.Path}", 98);
                            content.Text = workspace.Apply(new(WorkspaceAction.SaveKnowledge, title, reference), SaveWorkspace);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
                        { content.Text = "This reference could not be added."; }
                    };
                    entries.Children.Add(entry.Kind == "Instructions" ? ButtonRow(open, add) : open);
                    entries.Children.Add(content);
                }
                installed.Children.Add(new Expander { Header = $"{item.Repository} · {item.Commit[..12]}", Content = entries });
            }
        }

        ShowPreview();
        ShowInstalled();
        scanButton.Click += async (_, _) =>
        {
            busy(true);
            scanButton.IsEnabled = importButton.IsEnabled = false;
            scannedRepository = null;
            preview.Children.Clear();
            status.Text = "Scanning GitHub…";
            try
            {
                scannedRepository = await new RepositoryImport().ScanAsync(address.Text, closing.Token);
                status.Text = $"{scannedRepository.Repository} · {scannedRepository.Commit[..12]} · {scannedRepository.Findings.Count} items";
                ShowPreview();
            }
            catch (Exception error) when (!closing.IsCancellationRequested)
            {
                status.Text = error is InvalidOperationException ? error.Message : "The GitHub scan did not complete.";
                DiagnosticLog.Write("repository-import", $"Tools scan failed: {error.GetType().Name}");
            }
            finally
            {
                scanButton.IsEnabled = true;
                importButton.IsEnabled = scannedRepository is { Findings.Count: > 0 };
                busy(false);
            }
        };
        importButton.Click += (_, _) =>
        {
            if (scannedRepository is not { } scan) return;
            try { status.Text = InstallRepository(scan); ShowInstalled(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { status.Text = error.Message; }
        };
        return new ScrollViewer { Content = panel };
    }
}
