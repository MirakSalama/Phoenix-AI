using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NpuChat;

public sealed partial class MainWindow
{
    private readonly List<ChatAttachment> draftAttachments = [];
    private bool loadingAttachments;
    private int attachmentExcerptLimit;

    private void Attach_Click(object sender, RoutedEventArgs args)
    {
        if (phase != Phase.Ready || loadingAttachments) return;
        var files = new MenuFlyoutItem { Text = "Choose files", Icon = new FontIcon { Glyph = "\uE8A5" } };
        files.Click += async (_, _) => await ReadDraftAsync(async id =>
        {
            RequireAttachmentSpace();
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            await ReadFilesAsync(await picker.PickMultipleFilesAsync(), id);
        });
        var paste = new MenuFlyoutItem { Text = "Paste as attachment", Icon = new FontIcon { Glyph = "\uE77F" } };
        paste.Click += async (_, _) => await ReadDraftAsync(id => ReadClipboardAsync(Clipboard.GetContent(), id, true));
        var menu = new MenuFlyout
        {
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["PhoenixMenuPresenter"],
            Items = { files, paste }
        };
        menu.ShowAt(AttachButton);
    }

    private async void Prompt_Paste(object sender, TextControlPasteEventArgs args)
    {
        if (phase != Phase.Ready || loadingAttachments) return;
        DataPackageView data;
        try { data = Clipboard.GetContent(); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        if (!data.Contains(StandardDataFormats.Bitmap) && !data.Contains(StandardDataFormats.StorageItems) &&
            !data.Contains(StandardDataFormats.Text)) return;
        args.Handled = true;
        await ReadDraftAsync(id => ReadClipboardAsync(data, id, false));
    }

    private async Task ReadDraftAsync(Func<Guid, Task> load)
    {
        if (phase != Phase.Ready || loadingAttachments) return;
        Guid owner = chatId;
        loadingAttachments = true;
        AttachButton.IsEnabled = false;
        try { await load(owner); }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closing.IsCancellationRequested && owner == chatId && phase == Phase.Ready)
                FullStatus.Text = error is InvalidDataException ? error.Message : "Phoenix couldn’t read that attachment.";
            DiagnosticLog.Write("attachment", $"Could not prepare attachment: {error.GetType().Name}");
        }
        finally
        {
            loadingAttachments = false;
            if (!closing.IsCancellationRequested) AttachButton.IsEnabled = phase == Phase.Ready;
        }
    }

    private bool CanAddDraft(Guid owner) => !closing.IsCancellationRequested && owner == chatId && phase == Phase.Ready;

    private async Task ReadClipboardAsync(DataPackageView data, Guid owner, bool attachText)
    {
        if (data.Contains(StandardDataFormats.StorageItems) && (await data.GetStorageItemsAsync()).OfType<StorageFile>().ToArray() is { Length: > 0 } files)
        {
            await ReadFilesAsync(files, owner);
            return;
        }
        if (data.Contains(StandardDataFormats.Bitmap))
        {
            RequireAttachmentSpace();
            var reference = await data.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            AddDraft(await ChatAttachments.ReadImageAsync("Clipboard image.jpg", stream, closing.Token), owner);
            return;
        }
        if (!data.Contains(StandardDataFormats.Text))
            throw new InvalidDataException("Copy text, an image, or files first.");
        string text = await data.GetTextAsync();
        if (!CanAddDraft(owner)) return;
        if (!attachText && text.Length < 1200 && text.Count(character => character == '\n') < 12)
        {
            Prompt.SelectedText = text;
            return;
        }
        AddDraft(ChatAttachments.FromText("Pasted text.txt", text), owner);
    }

    private async Task ReadFilesAsync(IEnumerable<StorageFile> files, Guid owner)
    {
        int failures = 0;
        string? lastError = null;
        foreach (var file in files)
        {
            if (!CanAddDraft(owner)) return;
            RequireAttachmentSpace();
            try
            {
                using var stream = await file.OpenStreamForReadAsync();
                AddDraft(await ChatAttachments.ReadAsync(file.Name, stream, closing.Token), owner);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures++;
                lastError = error is InvalidDataException ? error.Message : "The file couldn’t be decoded.";
            }
        }
        if (failures > 0 && CanAddDraft(owner)) FullStatus.Text = failures == 1 ? lastError : $"{failures} attachments couldn’t be read. {lastError}";
    }

    private void RequireAttachmentSpace()
    {
        if (draftAttachments.Count >= ChatAttachments.MaxCount)
            throw new InvalidDataException($"You can attach up to {ChatAttachments.MaxCount} files per message.");
    }

    private void AddDraft(ChatAttachment attachment, Guid owner)
    {
        if (!CanAddDraft(owner)) return;
        RequireAttachmentSpace();
        draftAttachments.Add(attachment);
        RenderDraftAttachments();
        FullStatus.Text = attachment.Note is { } note
            ? (attachment.Image ? $"Image attached. {note}" : note)
            : attachment.Truncated ? "The attachment was saved as an excerpt." : "Attachment ready.";
    }

    private ChatAttachment[]? TakeDraftAttachments()
    {
        if (draftAttachments.Count == 0) return null;
        var files = draftAttachments.ToArray();
        ClearDraftAttachments();
        return files;
    }

    private void ClearDraftAttachments()
    {
        draftAttachments.Clear();
        RenderDraftAttachments();
    }

    private void RenderDraftAttachments()
    {
        DraftAttachmentList.Children.Clear();
        DraftAttachmentScroll.Visibility = draftAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var file in draftAttachments)
        {
            var row = AttachmentLabel(file);
            var remove = IconAction("\uE711", $"Remove {file.Name}");
            remove.Click += (_, _) =>
            {
                draftAttachments.Remove(file);
                RenderDraftAttachments();
                if (phase == Phase.Ready) FullStatus.Text = draftAttachments.Count == 0 ? "Ready" : "Attachments ready.";
            };
            row.Children.Add(remove);
            DraftAttachmentList.Children.Add(new Border { Child = row, CornerRadius = new CornerRadius(7),
                Background = (Brush)Application.Current.Resources["PhoenixPopupFill"], Padding = new Thickness(7, 3, 4, 3) });
        }
    }

    private StackPanel AttachmentLabel(ChatAttachment file)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (file.Preview is { Length: > 0 } jpeg)
        {
            var image = new Image { Width = 40, Height = 29, Stretch = Stretch.UniformToFill };
            row.Children.Add(image);
            _ = SetAttachmentPreviewAsync(image, jpeg);
        }
        else row.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = file.Name, MaxWidth = 175, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(label, file.Name);
        row.Children.Add(label);
        return row;
    }

    private static async Task SetAttachmentPreviewAsync(Image image, string jpeg)
    {
        try
        {
            if (jpeg.Length > 100_000) return;
            using var bytes = new MemoryStream(Convert.FromBase64String(jpeg));
            using var stream = bytes.AsRandomAccessStream();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            DiagnosticLog.Write("attachment", $"Could not show preview: {error.GetType().Name}");
        }
    }

    private void AddAttachmentCards(TextBlock body, IReadOnlyList<ChatAttachment>? files)
    {
        if (body.Parent is not StackPanel panel || files is null) return;
        foreach (var file in files.Take(ChatAttachments.MaxCount))
        {
            var content = new StackPanel { Spacing = 6 };
            if (file.Image || file.Note is not null)
                content.Children.Add(new TextBlock { Text = file.Note ?? "Text extracted from image", TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(file.Text))
                content.Children.Add(new ScrollViewer { MaxHeight = 230, Content = new TextBlock
                {
                    Text = file.Text, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap
                } });
            if (file.Truncated)
                content.Children.Add(new TextBlock { Text = "Saved as an excerpt.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new Expander { Header = AttachmentLabel(file), Content = content });
        }
    }
}
