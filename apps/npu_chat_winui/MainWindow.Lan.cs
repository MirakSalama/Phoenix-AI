using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace NpuChat;

public sealed partial class MainWindow
{
    private LanChatServer? lanServer;
    private readonly LanProfiles lanProfiles = new();
    private readonly Queue<LanProfile> lanQueue = new();
    private readonly DispatcherTimer lanQueueTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private LanProfile? activeLanProfile;
    private LocalModel lanFallbackModel;
    private string? desktopStatsBeforeLan;
    private bool lanChanging;
    private string? lanStatus;
    private Action<string?>? updateLanShare;
    private static string LanAdapterFile => Path.Combine(LanProfiles.Folder, "adapter.txt");
    private TaskCompletionSource<WebDictationResult>? webDictation;
    private TaskCompletionSource<WebSpeechResult>? webSpeech;
    private bool webVoiceAvailable;

    private Flyout CreateLanShareFlyout()
    {
        LanChatServer? qrServer = null;
        var picker = new PhoenixPicker
        {
            Header = "Local network adapter",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var toggle = new Button();
        var copy = IconAction("\uE8C8", "Copy access link");
        var certificate = new Button { Content = "Show trust certificate" };
        var qr = new Image { Width = 198, Height = 198, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(qr, "Phoenix LAN access QR code");
        var details = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12,
            Foreground = ToolBrush("Muted")
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { toggle, copy } };
        var panel = new StackPanel
        {
            Width = 280, Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Share Phoenix", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                qr, details, picker, actions, certificate
            }
        };
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["PhoenixFlyoutPresenter"],
            ShouldConstrainToRootBounds = false,
            Content = new ScrollViewer { Content = panel, MaxHeight = 440 }
        };

        void Update(string? message = null)
        {
            bool running = lanServer is not null;
            var shareable = lanChanging ? null : lanServer;
            if (qrServer != shareable)
            {
                qrServer = shareable;
                qr.Source = shareable is null ? null : CreateAccessQr(shareable.AccessLink);
            }
            qr.Visibility = shareable is not null ? Visibility.Visible : Visibility.Collapsed;
            picker.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            picker.IsEnabled = !lanChanging && picker.Items.Count > 0;
            toggle.Content = running ? "Stop LAN access" : "Start LAN access";
            toggle.IsEnabled = !lanChanging && (running || picker.SelectedItem is not null);
            copy.IsEnabled = certificate.IsEnabled = shareable is not null;
            certificate.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            details.Text = message ?? lanStatus ?? (lanChanging ? "Changing LAN access…" : shareable is { } server
                ? server.Address + "\nScan once to pair on this network. Share this code only with people you trust."
                : picker.Items.Count > 0 ? "Start LAN access to show the QR code." : "Connect this PC to a private IPv4 network.");
        }

        flyout.Opening += (_, _) =>
        {
            if (!lanChanging && lanServer is null)
            {
                LanInterface[] networks;
                try { networks = LanInterface.Available(); }
                catch (NetworkInformationException) { networks = []; }
                var selected = picker.SelectedItem;
                picker.ItemsSource = networks;
                int index = Array.IndexOf(networks, selected as LanInterface);
                picker.SelectedIndex = networks.Length == 0 ? -1 : Math.Max(0, index);
            }
            Update();
        };
        toggle.Click += async (_, _) =>
        {
            if (lanServer is not null || picker.SelectedItem is LanInterface)
                await ChangeLanAccessAsync(lanServer is null ? (LanInterface?)picker.SelectedItem : null, remember: true);
        };
        copy.Click += (_, _) =>
        {
            if (lanServer is not { } server) return;
            try
            {
                var data = new DataPackage();
                data.SetText(server.AccessLink);
                Clipboard.SetContent(data);
                Update("Access link copied.");
            }
            catch (COMException) { Update("Couldn’t copy the link. You can still scan the QR code."); }
        };
        certificate.Click += (_, _) =>
        {
            if (lanServer is not { } server) return;
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(LanChatServer.CertificatePath))!;
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
                Update("Copy Phoenix LAN certificate.cer to your own device and approve trust there.\n" +
                       $"SHA-256 fingerprint\n{server.AuthorityFingerprint}");
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Update("Could not open the certificate folder.");
            }
        };
        updateLanShare = Update;
        return flyout;
    }

    private async Task RestoreLanAccessAsync()
    {
        try
        {
            if (!File.Exists(LanAdapterFile)) return;
            string[] preference = File.ReadAllLines(LanAdapterFile);
            var candidates = LanInterface.Available().Where(network => network.Id == preference.FirstOrDefault()).ToArray();
            var network = candidates.FirstOrDefault(item => item.Address.ToString() == preference.ElementAtOrDefault(1))
                ?? candidates.FirstOrDefault();
            if (network is null)
            {
                lanStatus = "The saved LAN adapter is offline. Choose an active adapter to start sharing.";
                return;
            }
            await ChangeLanAccessAsync(network, remember: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NetworkInformationException)
        {
            DiagnosticLog.Write("lan", $"Could not read the saved LAN adapter: {error.GetType().Name}");
        }
    }

    private async Task ChangeLanAccessAsync(LanInterface? network, bool remember)
    {
        if (lanChanging || closing.IsCancellationRequested) return;
        lanChanging = true;
        lanStatus = null;
        updateLanShare?.Invoke(network is null ? "Stopping LAN access…" : "Starting HTTPS…");
        try
        {
            if (network is null)
            {
                await StopLanAccessAsync();
                if (remember) File.Delete(LanAdapterFile);
                return;
            }
            var server = await Task.Run(() => LanChatServer.StartAsync(network, lanProfiles,
                (profile, request) => OnLanDispatcherAsync(() => ReadWebChat(profile), request),
                (profile, turn, request) => OnLanDispatcherAsync(() => AcceptWebChat(profile, turn), request),
                (profile, selection, request) => OnLanDispatcherAsync(() => SelectWebChat(profile, selection), request),
                (profile, rating, request) => OnLanDispatcherAsync(() => RateWebReply(profile, rating), request),
                (profile, stop, request) => OnLanDispatcherAsync(() => StopWebLookup(profile, stop), request),
                TranscribeWebAsync, SpeakWebAsync, closing.Token));
            if (closing.IsCancellationRequested) { await server.DisposeAsync(); return; }
            lanServer = server;
            if (remember)
            {
                Directory.CreateDirectory(LanProfiles.Folder);
                File.WriteAllLines(LanAdapterFile, [network.Id, network.Address.ToString()]);
            }
            DiagnosticLog.Write("lan", "Started LAN browser access.");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or
                                       CryptographicException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            DiagnosticLog.Write("lan", $"Could not change LAN access: {error.GetType().Name}");
            lanStatus = "Could not change LAN access. Check the adapter, port 7443, and the diagnostic log.";
        }
        finally
        {
            lanChanging = false;
            if (!closing.IsCancellationRequested) updateLanShare?.Invoke(null);
        }
    }

    private static WriteableBitmap CreateAccessQr(string accessLink)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { Width = 198, Height = 198, Margin = 4, ErrorCorrection = ErrorCorrectionLevel.M }
        };
        var pixels = writer.Write(accessLink);
        var image = new WriteableBitmap(pixels.Width, pixels.Height);
        using var stream = image.PixelBuffer.AsStream();
        stream.Write(pixels.Pixels);
        image.Invalidate();
        return image;
    }

    private async Task StopLanAccessAsync()
    {
        var server = lanServer;
        lanServer = null;
        if (server is null) return;
        await server.DisposeAsync();
        DiagnosticLog.Write("lan", "Stopped LAN browser access.");
    }

    private async Task<T> OnLanDispatcherAsync<T>(Func<T> action, CancellationToken request)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (closing.IsCancellationRequested || request.IsCancellationRequested)
            {
                result.TrySetCanceled();
                return;
            }
            try { result.TrySetResult(action()); }
            catch (Exception error) { result.TrySetException(error); }
        }))
            throw new OperationCanceledException("Phoenix is closing.");
        return await result.Task.WaitAsync(request);
    }

    private bool LanWorkerReady => phase == Phase.Ready && !settingsOpen && !voiceEnabled &&
        currentReply is null && activeLanProfile is null;

    private static WebMessage ToWebMessage(Turn turn, int index)
    {
        if (turn.Role == "user") return new WebMessage(turn.Role, turn.Content, Attachments: ToWebAttachments(turn.Attachments));
        var reply = CloudResearch.SeparateResources(turn.Content, turn.Sources);
        var presented = ReplyPresentation.Split(reply.Text);
        return new WebMessage(turn.Role, ConversationSpeech.MarkCutoff(presented.Text, turn.Cutoff), turn.Speaker,
            reply.Sources.Select(source => new WebSource(source.Title, source.Url.AbsoluteUri)).ToArray(),
            turn.SourceNotes ?? reply.SourceNotes, presented.Snippets, CloudResearch.TextForSpeech(reply.Text),
            index, turn.Rating, presented.Text == turn.Content && turn.Cutoff is null ? null : turn.Content, turn.Activity,
            Execution: turn.Execution);
    }

    private static WebAttachment[]? ToWebAttachments(IReadOnlyList<ChatAttachment>? files) => files?.Select(file =>
        new WebAttachment(file.Name, file.Image, file.Preview, LocalWorkspace.Limit(file.Text, 1500),
            file.Note ?? (file.Truncated || file.Text.Length > 1500 ? "Showing an excerpt." : null))).ToArray();

    private WebChatState ReadWebChat(LanProfile profile)
    {
        bool active = activeLanProfile == profile;
        bool pending = active || profile.QueuedTurn is not null;
        var messages = profile.Data.Turns.Select(ToWebMessage).ToList();
        if (profile.QueuedTurn is { } queued)
            messages.Add(new WebMessage("user", queued.Text, Attachments: ToWebAttachments(queued.Attachments)));
        if (active && currentReply is { } reply)
            messages.Add(new WebMessage("assistant", reply.Text, reply.Speaker));
        string status = profile.Status ?? (phase == Phase.Failed ? "Phoenix is unavailable on the PC."
            : LanWorkerReady ? "Ready" : "PC is busy");
        var saved = (profile.Data.SavedChats ?? []).Select(chat =>
            new WebChatSummary(chat.ChatId, SavedChat.SessionTitle(chat.Turns, chat.CustomTitle))).ToArray();
        return new WebChatState(profile.Data.ChatId, SavedChat.SessionTitle(profile.Data.Turns, profile.Data.CustomTitle),
            !pending && phase != Phase.Failed, dictationAvailable, webVoiceAvailable, LanWorkerReady, pending, status, messages, saved);
    }

    private WebChatSelectionResult SelectWebChat(LanProfile profile, WebChatSelection selection)
    {
        if (selection.BranchReplyIndex is not null && selection.OpenChatId is not null)
            return new WebChatSelectionResult(400, Message: "Choose a chat or a reply to branch from.");
        if (selection.ChatId != profile.Data.ChatId)
            return new WebChatSelectionResult(409, Message: "This device’s open chat changed. Try again.");
        if (activeLanProfile == profile || profile.QueuedTurn is not null)
            return new WebChatSelectionResult(409, Message: "Wait for your current reply before switching chats.");
        try
        {
            bool opened = selection.BranchReplyIndex is int index
                ? profile.BranchChat(index, LanProfiles.Save) : profile.SelectChat(selection.OpenChatId, LanProfiles.Save);
            return opened
                ? new WebChatSelectionResult(200, ReadWebChat(profile))
                : new WebChatSelectionResult(404, Message: "This chat or reply is unavailable on your device.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Write("lan", $"Could not switch the LAN chat: {error.GetType().Name}");
            return new WebChatSelectionResult(503, Message: "Phoenix couldn’t save the chat change. Try again.");
        }
    }

    private bool StopWebLookup(LanProfile profile, WebStopLookup request)
    {
        if (activeLanProfile != profile || profile.Data.ChatId != request.ChatId ||
            request.UserIndex < 0 || request.UserIndex != profile.Data.Turns.Count - 1 ||
            profile.Data.Turns[^1].Role != "user" || lookupTurn is not { IsCancellationRequested: false })
            return false;
        StopLookup();
        return true;
    }

    private WebChatSelectionResult RateWebReply(LanProfile profile, WebReplyRating request)
    {
        if (request.ChatId != profile.Data.ChatId || activeLanProfile == profile || profile.QueuedTurn is not null)
            return new WebChatSelectionResult(409, Message: "Wait for the open chat to finish updating, then try again.");
        if (request.Rating is { } rating && !Enum.IsDefined(rating))
            return new WebChatSelectionResult(400, Message: "Choose thumbs up or thumbs down.");
        try
        {
            return profile.RateReply(request.ReplyIndex, request.Rating, LanProfiles.Save)
                ? new WebChatSelectionResult(200, ReadWebChat(profile))
                : new WebChatSelectionResult(404, Message: "This reply is unavailable on your device.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Write("lan", $"Could not save a LAN reply rating: {error.GetType().Name}");
            return new WebChatSelectionResult(503, Message: "Phoenix couldn’t save that rating.");
        }
    }

    private WebChatResult AcceptWebChat(LanProfile profile, WebChatRequest turn)
    {
        if (turn.ChatId != profile.Data.ChatId)
            return new WebChatResult(409, "This message belongs to a different chat.");
        if (phase == Phase.Failed)
            return new WebChatResult(503, "Phoenix is unavailable on the PC.");
        if (profile.QueuedTurn is not null || activeLanProfile == profile)
            return new WebChatResult(409, "Your conversation already has a pending message.");
        int userIndex = profile.Data.Turns.Count;
        profile.QueuedTurn = turn;
        profile.Status = "Queued";
        lanQueue.Enqueue(profile);
        lanQueueTimer.Start();
        RunNextLanTurn();
        return new WebChatResult(202, UserIndex: userIndex);
    }

    private void RunNextLanTurn()
    {
        if (lanQueue.Count == 0) { lanQueueTimer.Stop(); return; }
        if (closing.IsCancellationRequested || !LanWorkerReady) return;
        var profile = lanQueue.Dequeue();
        var turn = profile.QueuedTurn!;
        profile.QueuedTurn = null;
        if (lanQueue.Count == 0) lanQueueTimer.Stop();
        StopSelectedModel();
        activeBackend = LocalBackend.Auto;
        activeLanProfile = profile;
        lanFallbackModel = Enum.GetValues<LocalModel>().FirstOrDefault(ModelAvailable);
        desktopStatsBeforeLan = Stats.Text;
        profile.Workspace.LearnFromMessages = workspace.LearnFromMessages;
        _ = RunBrowserTurnAsync(profile, turn);
    }

    private async Task RunBrowserTurnAsync(LanProfile profile, WebChatRequest turn)
    {
        int userIndex = profile.Data.Turns.Count;
        SetPhase(Phase.PreparingTools, "Preparing reply…");
        try
        {
            await SendTurnAsync(turn.Text, turn.Attachments);
            if (activeLanProfile == profile && currentReply is null && !closing.IsCancellationRequested)
            {
                if (profile.Data.Turns.Count == userIndex) profile.Data.Turns.Add(new Turn("user", turn.Text, Attachments: turn.Attachments));
                FinishReply(profile.Status ?? "Phoenix couldn’t complete this message.");
            }
        }
        catch (Exception error)
        {
            if (closing.IsCancellationRequested || activeLanProfile != profile) return;
            DiagnosticLog.Write("lan", $"Browser turn failed: {error.GetType().Name}");
            if (profile.Data.Turns.Count == userIndex) profile.Data.Turns.Add(new Turn("user", turn.Text, Attachments: turn.Attachments));
            TurnError("Phoenix couldn’t complete that message. Check the desktop log.");
        }
    }

    private void CompleteLanTurn(LanProfile profile)
    {
        profile.Status = null;
        try { LanProfiles.Save(profile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Write("lan", $"Could not save the LAN chat: {error.GetType().Name}");
            profile.Status = "Chat history could not be saved.";
        }
        StopSelectedModel();
        activeBackend = LocalBackend.Auto;
        activeLanProfile = null;
        if (desktopStatsBeforeLan is { } desktopStats) Stats.Text = desktopStats;
        desktopStatsBeforeLan = null;
        SetPhase(Phase.Ready);
    }

    private async Task<WebDictationResult> TranscribeWebAsync(string pcm, CancellationToken request)
    {
        var job = await OnLanDispatcherAsync(() => StartWebDictationAsync(pcm), request);
        return await job.WaitAsync(request);
    }

    private async Task<WebDictationResult> StartWebDictationAsync(string pcm)
    {
        if (!dictationAvailable || engine is null)
            return new WebDictationResult(503, Message: "Local dictation is unavailable on the PC.");
        if (!LanWorkerReady || webDictation is not null)
            return new WebDictationResult(409, Message: "Phoenix is busy. Try dictation again when it’s ready.");

        var completion = new TaskCompletionSource<WebDictationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        webDictation = completion;
        SetPhase(Phase.Transcribing, "Transcribing LAN dictation…");
        try
        {
            SendEngine(new { kind = "web_dictate", id = ++requestId, pcm });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(90), closing.Token);
        }
        catch (TimeoutException)
        {
            if (webDictation == completion) BridgeStopped("LAN dictation timed out.", bridgeVoiceOnly);
            return new WebDictationResult(503, Message: "Dictation timed out. Try a shorter recording.");
        }
        finally { if (webDictation == completion) webDictation = null; }
    }

    private bool HandleWebDictation(string kind, System.Text.Json.JsonElement message)
    {
        if (webDictation is null || kind is not ("web_transcript" or "error")) return false;
        string text = message.GetProperty("text").GetString() ?? "";
        FinishWebDictation(kind == "web_transcript" ? new WebDictationResult(200, Text: text) :
            new WebDictationResult(503, Message: "Local dictation failed. Check the Phoenix log on your PC."));
        return true;
    }

    private void FinishWebDictation(WebDictationResult result)
    {
        if (webDictation is not { } completion) return;
        webDictation = null;
        SetPhase(Phase.Ready);
        completion.TrySetResult(result);
    }

    private async Task<WebSpeechResult> SpeakWebAsync(LanProfile profile, WebSpeechRequest request, CancellationToken cancellation)
    {
        var job = await OnLanDispatcherAsync(() => StartWebSpeechAsync(profile, request, cancellation), cancellation);
        return await job.WaitAsync(cancellation);
    }

    private async Task<WebSpeechResult> StartWebSpeechAsync(LanProfile profile, WebSpeechRequest request, CancellationToken cancellation)
    {
        if (!webVoiceAvailable || engine is null)
            return new WebSpeechResult(503, Message: "Kokoro voice is unavailable on the PC.");
        if (request.ChatId != profile.Data.ChatId || request.ReplyIndex < 0 || request.ReplyIndex >= profile.Data.Turns.Count ||
            profile.Data.Turns[request.ReplyIndex] is not { Role: "assistant" } reply)
            return new WebSpeechResult(404, Message: "This reply is unavailable in your conversation.");
        if (!LanWorkerReady || webSpeech is not null)
            return new WebSpeechResult(409, Message: "Phoenix is busy on the PC.");

        string prose = CloudResearch.SeparateResources(reply.Content, null).Text;
        string text = request.Brief ? ConversationSpeech.ForVoice(prose, reply.Cutoff is not null) : CloudResearch.TextForSpeech(prose);
        int offset = request.Offset;
        if (offset < 0 || offset > text.Length || (offset < text.Length && char.IsLowSurrogate(text[offset])))
            return new WebSpeechResult(400, Message: "Invalid speech position.");
        if (offset == text.Length) return new WebSpeechResult(200);
        int end = ConversationSpeech.NextSentenceEnd(text, offset);
        string phrase = text[offset..end].Trim();
        int? next = end < text.Length ? end : null;
        if (phrase.Length == 0) return new WebSpeechResult(200, NextOffset: next);

        var completion = new TaskCompletionSource<WebSpeechResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        webSpeech = completion;
        using var cancel = cancellation.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            if (webSpeech == completion) SendEngine(new { kind = "cancel" });
        }));
        SetPhase(Phase.PreparingVoice, "Preparing LAN voice…");
        try
        {
            SendEngine(new { kind = "web_speak", id = ++requestId, text = phrase });
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), closing.Token);
            return result with { NextOffset = result.Status == 200 ? next : null };
        }
        catch (TimeoutException)
        {
            if (webSpeech == completion) BridgeStopped("LAN speech timed out.", bridgeVoiceOnly);
            return new WebSpeechResult(503, Message: "Voice synthesis timed out.");
        }
        finally { if (webSpeech == completion) webSpeech = null; }
    }

    private bool HandleWebSpeech(string kind, System.Text.Json.JsonElement message)
    {
        if (webSpeech is null || kind is not ("web_audio" or "error")) return false;
        FinishWebSpeech(kind == "web_audio" ? new WebSpeechResult(200, Audio: message.GetProperty("audio").GetString()) :
            new WebSpeechResult(503, Message: "Local voice synthesis failed. Check the Phoenix log on your PC."));
        return true;
    }

    private void FinishWebSpeech(WebSpeechResult result)
    {
        if (webSpeech is not { } completion) return;
        webSpeech = null;
        SetPhase(Phase.Ready);
        completion.TrySetResult(result);
    }
}
