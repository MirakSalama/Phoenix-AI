using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace NpuChat;

internal sealed record ChatAttachment(string Name, string Text, bool Image = false, string? Preview = null,
    bool Truncated = false, string? Note = null);

internal static partial class ChatAttachments
{
    internal const int MaxCount = 4;
    internal const int MaxFileBytes = 4 * 1024 * 1024;
    internal const int MaxBatchBytes = 8 * 1024 * 1024;
    internal const int MaxText = 24_000;
    internal const string DefaultQuestion = "What can you tell me about the attached files?";
    private static readonly HashSet<string> ImageTypes = new(
        [".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff",
            ".webp", ".heic", ".heif", ".avif", ".ico"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions ContextJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static ChatAttachment FromText(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("This attachment has no text.");
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f' or '\u001b')))
            throw new InvalidDataException("Choose a text file, PDF, Excel workbook (.xlsx), or a supported image.");
        return new(CleanName(name), StorageExcerpt(text), Truncated: text.Length > MaxText);
    }

    internal static async Task<ChatAttachment> ReadAsync(string name, Stream source, CancellationToken cancel)
    {
        if (source.CanSeek && source.Length > MaxFileBytes) throw new InvalidDataException("Each attachment must be 4 MB or smaller.");
        using var contents = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, cancel)) != 0)
        {
            if (contents.Length + count > MaxFileBytes) throw new InvalidDataException("Each attachment must be 4 MB or smaller.");
            contents.Write(buffer, 0, count);
        }
        contents.Position = 0;
        string extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".pdf" or ".xlsx")
            return await DocumentAttachment.ReadAsync(CleanName(name), extension, contents, cancel);
        if (IsImage(name, contents.GetBuffer().AsSpan(0, (int)contents.Length)))
        {
            using var stream = contents.AsRandomAccessStream();
            try { return await ReadImageAsync(name, stream, cancel); }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                string message = extension is ".heic" or ".heif" or ".avif"
                    ? "Windows couldn’t open this image. Install the codec for this format or attach it as PNG or JPEG."
                    : "Windows couldn’t decode this image. Check that it opens normally or save a copy as PNG or JPEG.";
                throw new InvalidDataException(message, error);
            }
        }
        return DecodeText(name, contents.GetBuffer().AsSpan(0, (int)contents.Length));
    }

    private static bool IsImage(string name, ReadOnlySpan<byte> bytes)
    {
        bool bitmap = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            bytes.StartsWith(new byte[] { 255, 216, 255 }) || bytes.StartsWith("GIF8"u8) ||
            bytes.StartsWith("II\u002a\0"u8) || bytes.StartsWith("MM\0\u002a"u8) ||
            (bytes.StartsWith("RIFF"u8) && bytes.Length >= 12 && bytes[8..12].SequenceEqual("WEBP"u8));
        return bitmap || ImageTypes.Contains(Path.GetExtension(name));
    }

    private static ChatAttachment DecodeText(string name, ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.StartsWith(Encoding.Unicode.GetPreamble()))
                return FromText(name, new System.Text.UnicodeEncoding(false, false, true).GetString(bytes[2..]));
            if (bytes.StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
                return FromText(name, new System.Text.UnicodeEncoding(true, false, true).GetString(bytes[2..]));
            if (bytes.StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
            return FromText(name, Utf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("This file could not be read. Choose a UTF-8 or UTF-16 text file, PDF, .xlsx workbook, or a supported image.");
        }
    }

    internal static async Task<ChatAttachment> ReadImageAsync(string name, IRandomAccessStream source, CancellationToken cancel)
    {
        if (source.Size > 32 * 1024 * 1024) throw new InvalidDataException("The clipboard image is too large. Resize it and try again.");
        var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancel);
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 48_000_000)
            throw new InvalidDataException("This image is too large. Resize it and try again.");
        using var thumbnail = await DecodeAsync(decoder, 240, cancel);
        using var preview = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, preview).AsTask(cancel);
        encoder.SetSoftwareBitmap(thumbnail);
        await encoder.FlushAsync().AsTask(cancel);
        preview.Seek(0);
        using var reader = new DataReader(preview);
        uint size = await reader.LoadAsync((uint)preview.Size).AsTask(cancel);
        byte[] jpeg = new byte[size];
        reader.ReadBytes(jpeg);

        string text = "";
        string? note = "No readable text found.";
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) note = "Image text recognition unavailable on this PC.";
            else
            {
                using var bitmap = await DecodeAsync(decoder, Math.Min(2560u, OcrEngine.MaxImageDimension), cancel);
                var result = await engine.RecognizeAsync(bitmap).AsTask(cancel);
                text = string.Join("\n", result.Lines.Select(line => line.Text)).Trim();
                if (text.Length > 0) note = null;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            DiagnosticLog.Write("attachment", $"OCR unavailable: {error.GetType().Name}");
            note = "Image text recognition unavailable on this PC.";
        }
        return new(CleanName(name), StorageExcerpt(text), Image: true, Preview: Convert.ToBase64String(jpeg),
            Truncated: text.Length > MaxText, Note: note);
    }

    private static Task<SoftwareBitmap> DecodeAsync(BitmapDecoder decoder, uint longestSide, CancellationToken cancel)
    {
        double scale = Math.Min(1, longestSide / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform
        {
            ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * scale))
        }, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancel);
    }

    internal static string CleanName(string name)
    {
        string leaf = name.Split(['/', '\\']).LastOrDefault() ?? "";
        string label = new string(leaf.Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();
        return label.Length == 0 ? "Attachment" : label;
    }

    internal static string Context(IReadOnlyList<ChatAttachment> items, string question, int budget)
    {
        var terms = Words().Matches(question).Select(match => match.Value).Take(24).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var documentIndices = Enumerable.Range(0, items.Count).Where(i =>
            Path.GetExtension(items[i].Name).ToLowerInvariant() is ".pdf" or ".xlsx").ToArray();
        var otherIndices = Enumerable.Range(0, items.Count).Except(documentIndices).ToArray();
        var limits = new int[items.Count];
        int Allocate(int[] indices, int available)
        {
            int leftover = available;
            int pending = indices.Length;
            foreach (int index in indices.OrderBy(i => items[i].Text.Length))
            {
                limits[index] = Math.Min(items[index].Text.Length, leftover / pending--);
                leftover -= limits[index];
            }
            return available - leftover;
        }
        int reserve = otherIndices.Length * Math.Min(250, budget / Math.Max(1, items.Count));
        int documentUsed = Allocate(documentIndices, Math.Max(0, budget - reserve));
        Allocate(otherIndices, Math.Max(0, budget - documentUsed));
        return JsonSerializer.Serialize(items.Select((item, index) =>
        {
            int perFile = limits[index];
            string text = item.Text;
            if (perFile < 4 && text.Length > 0)
                return new { name = item.Name, kind = "text", text = "", note = item.Note, excerpt = true };
            if (text.Length > perFile)
            {
                // Keep the opening and the most relevant later excerpt. Logs often end with the failure.
                int opening = perFile / 3;
                var passages = Enumerable.Range(0, (text.Length + 449) / 450)
                    .Select(index => index * 450)
                    .Where(start => start >= opening)
                    .Select(start => (Start: start, Score: Words().Matches(text, start).TakeWhile(word =>
                        word.Index < start + 600).Count(word => terms.Contains(word.Value))));
                int best = passages.OrderByDescending(part => part.Score).ThenByDescending(part => part.Start)
                    .Select(part => part.Start).FirstOrDefault();
                int tail = perFile - opening;
                int start = best > 0 ? Math.Min(best, Math.Max(opening, text.Length - tail)) : Math.Max(opening, text.Length - tail);
                if (char.IsLowSurrogate(text[start])) start++;
                text = Slice(text, opening) + "\n[Excerpt omitted]\n" + Slice(text[start..], tail);
            }
            string kind = item.Image ? "image OCR" : Path.GetExtension(item.Name).ToLowerInvariant() switch
            {
                ".pdf" => "PDF",
                ".xlsx" => "Excel workbook",
                _ => "text"
            };
            return new { name = item.Name, kind, text,
                note = item.Note, excerpt = item.Truncated || item.Text.Length > perFile };
        }), ContextJson);
    }

    private static string StorageExcerpt(string text)
    {
        if (text.Length <= MaxText) return text;
        const string omission = "\n[Middle of attachment omitted]\n";
        int tail = text.Length - (MaxText - MaxText / 2 - omission.Length);
        if (char.IsLowSurrogate(text[tail])) tail++;
        return Slice(text, MaxText / 2) + omission + text[tail..];
    }

    private static string Slice(string text, int limit) =>
        text.Length <= limit ? text : text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];

    [GeneratedRegex(@"\b[\p{L}\p{N}_-]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
