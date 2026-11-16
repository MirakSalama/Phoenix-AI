using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace NpuChat;

internal sealed record LanInterface(string Id, string Name, IPAddress Address, IPAddress Mask)
{
    public override string ToString() => $"{Name} · {Address}";

    internal static LanInterface[] Available() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up &&
            network.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses
            .Where(entry => IsPrivateV4(entry.Address))
            .Select(entry => new LanInterface(network.Id, network.Name, entry.Address, entry.IPv4Mask)))
        .OrderBy(network => network.Name).ToArray();

    internal bool Includes(IPAddress? remote)
    {
        if (remote?.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] local = Address.GetAddressBytes(), other = remote.GetAddressBytes(), mask = Mask.GetAddressBytes();
        for (int i = 0; i < local.Length; i++)
            if ((local[i] & mask[i]) != (other[i] & mask[i])) return false;
        return true;
    }

    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}

internal sealed record WebSource(string Title, string Url);
internal sealed record WebAttachment(string Name, bool Image, string? Preview, string? Text, string? Note);
internal sealed record WebMessage(string Role, string Text, string? Speaker = null,
    IReadOnlyList<WebSource>? Sources = null, string? SourceNotes = null, IReadOnlyList<ReplySnippet>? Snippets = null,
    string? Speech = null, int? ReplyIndex = null, ReplyRating? Rating = null, string? CopyText = null,
    IReadOnlyList<ToolActivity>? Activity = null, IReadOnlyList<WebAttachment>? Attachments = null,
    ReplyExecution? Execution = null);
internal sealed record WebChatSummary(Guid ChatId, string Title);
internal sealed record WebChatState(Guid ChatId, string Title, bool CanSend, bool CanDictate, bool CanSpeak, bool VoiceReady,
    bool Pending, string Status, IReadOnlyList<WebMessage> Messages, IReadOnlyList<WebChatSummary> SavedChats);
internal sealed record WebChatRequest(Guid ChatId, string Text, IReadOnlyList<ChatAttachment>? Attachments = null);
internal sealed record WebAttachmentUpload(string? Name, byte[]? Content);
internal sealed record WebChatUpload(Guid ChatId, string? Text, WebAttachmentUpload?[]? Attachments);
internal sealed record WebChatSelection(Guid ChatId, Guid? OpenChatId, int? BranchReplyIndex = null);
internal sealed record WebReplyRating(Guid ChatId, int ReplyIndex, ReplyRating? Rating);
internal sealed record WebStopLookup(Guid ChatId, int UserIndex);
internal sealed record WebChatSelectionResult(int Status, WebChatState? Chat = null, string? Message = null);
internal sealed record WebChatResult(int Status, string? Message = null, int? UserIndex = null);
internal sealed record WebDictationResult(int Status, string? Text = null, string? Message = null);
internal sealed record WebSpeechRequest(Guid ChatId, int ReplyIndex, int Offset, bool Brief = false);
internal sealed record WebSpeechResult(int Status, string? Audio = null, int? NextOffset = null, string? Message = null);

internal sealed class LanChatServer : IAsyncDisposable
{
    private const int Port = 7443;
    private const int MaxAudioBytes = 30 * 16_000 * 2;
    private const int MaxChatUploadBytes = 12 * 1024 * 1024;
    private static readonly object ProfileKey = new();
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;

    internal static string CertificatePath => Path.Combine(DiagnosticLog.Folder, "..", "LAN", "Phoenix LAN certificate.cer");
    internal string Address { get; }
    internal string AccessLink { get; }
    internal string AuthorityFingerprint { get; }

    private LanChatServer(WebApplication app, X509Certificate2 certificate, IPAddress address, string code, byte[] authority)
    {
        this.app = app;
        this.certificate = certificate;
        Address = $"https://{address}:{Port}/";
        AccessLink = Address + "#access=" + code;
        AuthorityFingerprint = Convert.ToHexString(SHA256.HashData(authority));
    }

    internal static async Task<LanChatServer> StartAsync(LanInterface network,
        LanProfiles profiles,
        Func<LanProfile, CancellationToken, Task<WebChatState>> read,
        Func<LanProfile, WebChatRequest, CancellationToken, Task<WebChatResult>> send,
        Func<LanProfile, WebChatSelection, CancellationToken, Task<WebChatSelectionResult>> select,
        Func<LanProfile, WebReplyRating, CancellationToken, Task<WebChatSelectionResult>> rate,
        Func<LanProfile, WebStopLookup, CancellationToken, Task<bool>> stopLookup,
        Func<string, CancellationToken, Task<WebDictationResult>> transcribe,
        Func<LanProfile, WebSpeechRequest, CancellationToken, Task<WebSpeechResult>> speak,
        CancellationToken stop)
    {
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        byte[] authorization = Encoding.ASCII.GetBytes("Bearer " + code);
        var (certificate, authority) = CreateCertificate(network.Address);
        LanChatServer? server = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 128 * 1024;
                options.Listen(network.Address, Port, endpoint => endpoint.UseHttps(certificate));
            });
            var app = builder.Build();
            var attachmentReader = new SemaphoreSlim(1, 1);
            server = new LanChatServer(app, certificate, network.Address, code, authority);
            app.Use(async (context, next) =>
            {
                if (!network.Includes(context.Connection.RemoteIpAddress) ||
                    !string.Equals(context.Request.Host.Host, network.Address.ToString(), StringComparison.Ordinal) ||
                    context.Request.Host.Port != Port)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                var headers = context.Response.Headers;
                headers.CacheControl = "no-store";
                headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; " +
                    "connect-src 'self'; img-src 'self' data:; manifest-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
                headers["Referrer-Policy"] = "no-referrer";
                headers["X-Content-Type-Options"] = "nosniff";
                if (context.Request.Path.StartsWithSegments("/api") &&
                    !string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/pair", StringComparison.OrdinalIgnoreCase))
                {
                    string header = context.Request.Headers.Authorization.ToString();
                    try
                    {
                        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || profiles.Find(header[7..]) is not { } profile)
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }
                        context.Items[ProfileKey] = profile;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    {
                        DiagnosticLog.Write("lan", $"Could not load a LAN profile: {error.GetType().Name}");
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        return;
                    }
                }
                await next(context);
            });
            string assets = Path.Combine(AppContext.BaseDirectory, "WebChat");
            app.MapGet("/", () => Results.File(Path.Combine(assets, "index.html"), "text/html; charset=utf-8"));
            app.MapGet("/phoenix.css", () => Results.File(Path.Combine(assets, "phoenix.css"), "text/css; charset=utf-8"));
            app.MapGet("/phoenix.js", () => Results.File(Path.Combine(assets, "phoenix.js"), "text/javascript; charset=utf-8"));
            app.MapGet("/phoenix.webmanifest", () => Results.File(Path.Combine(assets, "phoenix.webmanifest"), "application/manifest+json"));
            app.MapGet("/phoenix-192.png", () => Results.File(Path.Combine(assets, "phoenix-192.png"), "image/png"));
            app.MapGet("/phoenix-512.png", () => Results.File(Path.Combine(assets, "phoenix-512.png"), "image/png"));
            app.MapGet("/phoenix-lan.cer", () => Results.File(authority, "application/x-x509-ca-cert", "Phoenix LAN certificate.cer"));
            app.MapGet("/dictation-worklet.js", () => Results.File(Path.Combine(assets, "dictation-worklet.js"), "text/javascript; charset=utf-8"));
            app.MapGet("/phoenix.svg", () => Results.File(Path.Combine(AppContext.BaseDirectory, "Assets", "Phoenix.svg"), "image/svg+xml"));
            app.MapPost("/api/pair", (HttpContext context) =>
            {
                if (!CryptographicOperations.FixedTimeEquals(authorization,
                    Encoding.ASCII.GetBytes(context.Request.Headers.Authorization.ToString())))
                    return Results.Unauthorized();
                try { return Results.Json(new { token = profiles.Pair() }, statusCode: 201); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Write("lan", $"Could not save a LAN profile: {error.GetType().Name}");
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            });
            app.MapGet("/api/chat", async (HttpContext context, CancellationToken request) =>
                Results.Json(await read(Profile(context), request)));
            app.MapPost("/api/chat", async (HttpContext context, CancellationToken request) =>
            {
                if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
                context.Features.GetRequiredFeature<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = MaxChatUploadBytes;
                if (context.Request.ContentLength > MaxChatUploadBytes)
                    return Results.Json(new { message = "The message and attachments are too large." }, statusCode: 413);
                WebChatUpload? upload;
                try { upload = await context.Request.ReadFromJsonAsync<WebChatUpload>(request); }
                catch (BadHttpRequestException error) when (error.StatusCode == 413)
                {
                    return Results.Json(new { message = "The message and attachments are too large." }, statusCode: 413);
                }
                catch (System.Text.Json.JsonException)
                {
                    return Results.Json(new { message = "The message or an attachment could not be decoded." }, statusCode: 400);
                }
                string text = upload?.Text?.Trim() ?? "";
                var files = upload?.Attachments ?? [];
                if (upload is null || text.Length > 32_000 || (text.Length == 0 && files.Length == 0))
                    return Results.Json(new { message = "Enter a message of up to 32,000 characters or attach a file." }, statusCode: 400);
                if (files.Length > ChatAttachments.MaxCount || files.Any(file =>
                    file?.Name is not { Length: > 0 and <= 255 } || file.Content is not { Length: > 0 and <= ChatAttachments.MaxFileBytes }) ||
                    files.Sum(file => (long)file!.Content!.Length) > ChatAttachments.MaxBatchBytes)
                    return Results.Json(new { message = "Attach up to 4 files, with up to 4 MB per file and 8 MB total." }, statusCode: 400);
                List<ChatAttachment>? attachments = null;
                if (files.Length > 0)
                {
                    if (!await attachmentReader.WaitAsync(0, request))
                        return Results.Json(new { message = "Phoenix is reading another attachment. Try again in a moment." }, statusCode: 409);
                    try
                    {
                        attachments = [];
                        foreach (var file in files)
                        {
                            using var stream = new MemoryStream(file!.Content!, writable: false);
                            attachments.Add(await ChatAttachments.ReadAsync(file.Name!, stream, request));
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        DiagnosticLog.Write("attachment", $"LAN attachment could not be read: {error.GetType().Name}");
                        return Results.Json(new { message = error is InvalidDataException ? error.Message :
                            "Phoenix couldn’t decode one of the attachments. Try a text file or a PNG or JPEG image." }, statusCode: 400);
                    }
                    finally { attachmentReader.Release(); }
                }
                var result = await send(Profile(context), new WebChatRequest(upload.ChatId,
                    text.Length > 0 ? text : ChatAttachments.DefaultQuestion, attachments), request);
                return Results.Json(new { message = result.Message, userIndex = result.UserIndex }, statusCode: result.Status);
            });
            app.MapPost("/api/chat/select", async (HttpContext context, WebChatSelection selection, CancellationToken request) =>
            {
                var result = await select(Profile(context), selection, request);
                return Results.Json(new { chat = result.Chat, message = result.Message }, statusCode: result.Status);
            });
            app.MapPost("/api/chat/rating", async (HttpContext context, WebReplyRating rating, CancellationToken request) =>
            {
                var result = await rate(Profile(context), rating, request);
                return Results.Json(new { chat = result.Chat, message = result.Message }, statusCode: result.Status);
            });
            app.MapPost("/api/chat/stop-lookup", async (HttpContext context, WebStopLookup lookup, CancellationToken request) =>
                Results.Json(new { stopped = await stopLookup(Profile(context), lookup, request) }));
            app.MapPost("/api/speech", async (HttpContext context, WebSpeechRequest turn, CancellationToken request) =>
            {
                var result = await speak(Profile(context), turn, request);
                return Results.Json(new { audio = result.Audio, nextOffset = result.NextOffset, message = result.Message }, statusCode: result.Status);
            });
            app.MapPost("/api/dictation", async (HttpContext context, CancellationToken request) =>
            {
                if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                context.Features.GetRequiredFeature<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = MaxAudioBytes;
                if (context.Request.ContentLength > MaxAudioBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                using var audio = new MemoryStream();
                await context.Request.Body.CopyToAsync(audio, request);
                if (audio.Length == 0 || audio.Length % 2 != 0)
                    return Results.Json(new { message = "The recording did not contain valid audio." }, statusCode: 400);
                var result = await transcribe(Convert.ToBase64String(audio.GetBuffer(), 0, (int)audio.Length), request);
                return Results.Json(new { text = result.Text, message = result.Message }, statusCode: result.Status);
            });
            await app.StartAsync(stop);
            return server;
        }
        catch
        {
            if (server is not null) await server.DisposeAsync();
            else certificate.Dispose();
            throw;
        }
    }

    private static LanProfile Profile(HttpContext context) => (LanProfile)context.Items[ProfileKey]!;

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(timeout.Token); }
        finally
        {
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }

    private static (X509Certificate2 Certificate, byte[] Authority) CreateCertificate(IPAddress address)
    {
        string publicPath = Path.GetFullPath(CertificatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        using var authorityLock = File.Open(publicPath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        string privatePath = Path.ChangeExtension(publicPath, ".dpapi");
        var now = DateTimeOffset.UtcNow;
        using var root = LoadAuthority(privatePath, now);
        byte[] authority = root.RawData;
        File.WriteAllBytes(publicPath, authority);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Phoenix AI LAN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature |
            X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.Create(root, now.AddMinutes(-1), now.AddDays(30), RandomNumberGenerator.GetBytes(16));
        using var keyed = signed.CopyWithPrivateKey(key);
        // Windows Schannel needs a Windows key container for the TLS private key.
        byte[] pfx = keyed.Export(X509ContentType.Pfx);
        try { return (LoadPfx(pfx, X509KeyStorageFlags.UserKeySet), authority); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static X509Certificate2 LoadAuthority(string path, DateTimeOffset now)
    {
        if (File.Exists(path))
        {
            byte[] saved = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                var stored = LoadPfx(saved, X509KeyStorageFlags.EphemeralKeySet);
                if (stored.NotAfter.ToUniversalTime() > now.UtcDateTime.AddDays(31)) return stored;
                stored.Dispose();
            }
            finally { CryptographicOperations.ZeroMemory(saved); }
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Phoenix AI LAN Certificate Authority", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign |
            X509KeyUsageFlags.CrlSign, true));
        var root = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(3));
        byte[] privateKey = root.Export(X509ContentType.Pfx);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(privateKey, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, path, overwrite: true);
            return root;
        }
        catch { root.Dispose(); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            File.Delete(temporary);
        }
    }

    private static X509Certificate2 LoadPfx(byte[] data, X509KeyStorageFlags storage)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(data, null, storage);
#else
        return new X509Certificate2(data, (string?)null, storage);
#endif
    }
}
