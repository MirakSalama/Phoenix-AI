using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NpuChat;

public sealed partial class MainWindow
{
    private void History_Click(object sender, RoutedEventArgs args)
    {
        if (settingsOpen) return;
        if (HistoryView.IsPaneOpen)
        {
            SetHistoryPane(open: false);
            return;
        }
        RefreshChatHistory();
        SetHistoryPane(open: true);
        DiagnosticLog.Write("history", $"Opened history pane with {HistoryList.Items.Count} chats.");
    }

    private void CloseHistory_Click(object sender, RoutedEventArgs args)
    {
        SetHistoryPane(open: false);
        HistoryButton.Focus(FocusState.Programmatic);
    }

    private void SetHistoryPane(bool open) => HistoryView.IsPaneOpen = open;

    private void HistoryPane_Changed(SplitView sender, object args) =>
        SetActionName(HistoryButton, sender.IsPaneOpen ? "Hide chat history" : "Show chat history");

    private void RefreshChatHistory()
    {
        try
        {
            var chats = chatStore.ReadAll();
            HistoryList.Items.Clear();
            foreach (var chat in chats)
            {
                var row = new Grid { ColumnSpacing = 4, Tag = chat };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                details.Children.Add(new TextBlock { Text = chat.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                details.Children.Add(new TextBlock
                {
                    Text = $"{chat.Updated.ToLocalTime():g} · {chat.Device ?? ModelName(chat.Model)}",
                    FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Brush)Surface.Resources["Muted"]
                });
                row.Children.Add(details);
                var rename = new MenuFlyoutItem { Text = "Rename", Icon = new SymbolIcon(Symbol.Edit) };
                rename.Click += async (_, _) => await EditChatAsync(chat, delete: false);
                var delete = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
                delete.Click += async (_, _) => await EditChatAsync(chat, delete: true);
                var menu = new MenuFlyout
                {
                    MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["PhoenixMenuPresenter"],
                    Items = { rename, delete }
                };
                var actions = IconAction("\uE712", $"Actions for {chat.Title}");
                actions.Click += (_, _) => menu.ShowAt(actions);
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
                var item = new ListViewItem
                {
                    Content = row, Tag = chat, Padding = new Thickness(5, 4, 3, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(item, chat.DisplayTitle);
                HistoryList.Items.Add(item);
                if (chat.Id == chatId) HistoryList.SelectedItem = item;
            }
            HistoryEmpty.Text = "No saved chats yet.";
            HistoryEmpty.Visibility = chats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("history", $"Could not open history: {error.GetType().Name}");
            HistoryEmpty.Text = "Chat history could not be opened.";
            HistoryEmpty.Visibility = Visibility.Visible;
        }
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (settingsOpen || !HistoryList.IsEnabled ||
            args.ClickedItem is not FrameworkElement { Tag: SavedChat selected }) return;
        SetHistoryPane(open: false);
        if (selected.Id != chatId) LoadSavedChat(selected);
        Prompt.Focus(FocusState.Programmatic);
    }

    private void LoadSavedChat(SavedChat chat)
    {
        ResetTranscript(chat.Id);
        chatTitle = chat.CustomTitle;
        chatDevice = chat.Device;
        history.AddRange(chat.Turns);
        for (int index = 0; index < history.Count; index++)
        {
            var turn = history[index];
            if (turn.Role == "user")
            {
                var (_, userBody) = AddMessage("YOU", turn.Content, scroll: false);
                AddAttachmentCards(userBody, turn.Attachments);
                LastUser.Text = turn.Content;
                continue;
            }
            string speaker = turn.Speaker ?? "LOCAL";
            var reply = CloudResearch.SeparateResources(turn.Content, turn.Sources);
            string display = reply.Text.Length > 0 ? reply.Text : "I’ve put the resources below.";
            var presented = ReplyPresentation.Split(display);
            string visible = ConversationSpeech.MarkCutoff(presented.Text.Length > 0 ? presented.Text : "The code is in the chat.", turn.Cutoff);
            var (_, body) = AddMessage(speaker, visible, scroll: false);
            AddReplySnippets(body, presented.Snippets);
            LastReply.Text = visible;
            SetReplySource(speaker);
            AddCitations(reply.Sources, turn.SourceNotes ?? reply.SourceNotes, body);
            AddReplyDetails(body, turn.Activity, turn.Execution);
            AddReplyActions(body, index);
        }
        if (chat.Model != selectedModel && ModelAvailable(chat.Model))
        {
            StopSelectedModel();
            ReleaseWarmWorker();
            SetModelSelection(chat.Model);
            if (SelectedWorkMode == WorkMode.Manual) RouteLocal();
            else SetPhase(Phase.Ready);
        }
        lastContextUsage = chat.Context;
        UpdateContextCapsule();
        ScrollToEnd();
    }

    private async Task EditChatAsync(SavedChat chat, bool delete)
    {
        if (settingsOpen || !HistoryList.IsEnabled) return;
        settingsOpen = true;
        try
        {
            var title = new TextBox { Text = chat.Title, MaxLength = 100, Header = "Chat name" };
            var dialog = WorkspaceDialog(delete ? "Delete chat?" : "Rename chat",
                delete ? new TextBlock { Text = chat.Title, TextWrapping = TextWrapping.Wrap } : title);
            dialog.PrimaryButtonText = delete ? "Delete" : "Save";
            dialog.CloseButtonText = "Cancel";
            dialog.DefaultButton = delete ? ContentDialogButton.Close : ContentDialogButton.Primary;
            title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing.IsCancellationRequested) return;
            if (delete)
            {
                chatStore.Delete(chat.Id);
                if (chat.Id == chatId) ResetTranscript(Guid.NewGuid());
            }
            else
            {
                string name = title.Text.Trim();
                chatStore.Save(chat with { CustomTitle = name });
                if (chat.Id == chatId) chatTitle = name;
            }
            RefreshChatHistory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write("history", $"Could not update chat {chat.Id:N}: {error.GetType().Name}");
            FullStatus.Text = "The chat could not be updated.";
        }
        finally { settingsOpen = false; }
    }
}
