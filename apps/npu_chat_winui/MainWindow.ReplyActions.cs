using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace NpuChat;

public sealed partial class MainWindow
{
    private Button? replyReadButton;

    private void AddReplyActions(TextBlock? body, int index)
    {
        if (body?.Parent is not StackPanel card || Turn.ReplyAt(history, index) is not { } turn) return;
        var copy = IconAction("\uE8C8", "Copy response");
        var helpful = IconAction("\uE8E1", "Thumbs up");
        var unhelpful = IconAction("\uE8E0", "Thumbs down");
        var read = IconAction("\uE767", "Read aloud");
        var branch = IconAction("\uE8B1", "Branch in new chat");
        string speech = CloudResearch.TextForSpeech(CloudResearch.SeparateResources(turn.Content, null).Text);
        read.IsEnabled = speech.Length > 0;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var button in new[] { copy, helpful, unhelpful, read, branch })
        {
            button.Width = 32;
            button.Height = 30;
            row.Children.Add(button);
        }
        card.Children.Add(row);

        void ShowRating()
        {
            var rating = Turn.ReplyAt(history, index)?.Rating;
            SetRatingAppearance(helpful, "Thumbs up", rating == ReplyRating.Helpful);
            SetRatingAppearance(unhelpful, "Thumbs down", rating == ReplyRating.Unhelpful);
        }
        ShowRating();
        helpful.Click += (_, _) => { RateDesktopReply(index, ReplyRating.Helpful); ShowRating(); };
        unhelpful.Click += (_, _) => { RateDesktopReply(index, ReplyRating.Unhelpful); ShowRating(); };
        read.Click += (_, _) => ReadDesktopReply(read, speech);
        branch.Click += (_, _) => BranchDesktopChat(index);
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(turn.Content);
                Clipboard.SetContent(data);
                FullStatus.Text = "Response copied.";
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                FullStatus.Text = "The clipboard is unavailable.";
            }
        };
    }

    private void SetRatingAppearance(Button button, string name, bool selected)
    {
        if (button.Content is FontIcon icon) icon.Foreground = (Brush)Surface.Resources[selected ? "Mint" : "Muted"];
        SetActionName(button, selected ? $"Remove {name.ToLowerInvariant()} rating" : name);
    }

    private bool CanEditDesktopChat => HistoryButton.IsEnabled && !settingsOpen && currentReply is null && activeLanProfile is null;

    private void RateDesktopReply(int index, ReplyRating rating)
    {
        if (!CanEditDesktopChat || Turn.ReplyAt(history, index) is not { } reply) return;
        var selected = reply.Rating == rating ? (ReplyRating?)null : rating;
        history[index] = reply with { Rating = selected };
        if (SaveChat()) FullStatus.Text = selected is null ? "Rating removed." : "Rating saved.";
        else
        {
            history[index] = reply;
            FullStatus.Text = "The rating could not be saved.";
        }
    }

    private void BranchDesktopChat(int index)
    {
        if (!CanEditDesktopChat || Turn.BranchThrough(history, index) is not { } turns) return;
        var branch = new SavedChat(Guid.NewGuid(), DateTimeOffset.UtcNow, selectedModel, turns,
            CustomTitle: SavedChat.BranchTitle(history, chatTitle), Device: chatDevice);
        try
        {
            if (!SaveChat())
            {
                FullStatus.Text = "Save the original chat before branching.";
                return;
            }
            chatStore.Save(branch);
            SetHistoryPane(open: false);
            string draft = Prompt.Text;
            LoadSavedChat(branch);
            Prompt.Text = draft;
            Prompt.Focus(FocusState.Programmatic);
            FullStatus.Text = "Opened a branch in a new chat.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write("history", $"Could not branch chat: {error.GetType().Name}");
            FullStatus.Text = "The branch could not be saved.";
        }
    }

    private void ReadDesktopReply(Button button, string speech)
    {
        if (replyReadButton == button)
        {
            ClearReplyReader();
            ++requestId;
            SendEngine(new { kind = "cancel" });
            playbackLevels = [];
            playbackStart = 0;
            SetPhase(Phase.Ready);
            return;
        }
        if (phase != Phase.Ready || voiceEnabled || settingsOpen) return;
        if (!webVoiceAvailable || engine is null)
        {
            FullStatus.Text = "Kokoro is unavailable.";
            return;
        }
        replyReadButton = button;
        SetReaderIcon(button, active: true);
        SpeakReply(speech);
    }

    private void ClearReplyReader()
    {
        if (replyReadButton is not { } button) return;
        replyReadButton = null;
        SetReaderIcon(button, active: false);
    }

    private static void SetReaderIcon(Button button, bool active)
    {
        if (button.Content is FontIcon icon) icon.Glyph = active ? "\uE71A" : "\uE767";
        SetActionName(button, active ? "Stop reading" : "Read aloud");
    }
}
