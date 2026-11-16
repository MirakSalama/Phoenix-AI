using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NpuChat;

internal static class DocumentAttachment
{
    private static readonly SemaphoreSlim ReaderSlot = new(1);

    internal static async Task<ChatAttachment> ReadAsync(string name, string extension, Stream source, CancellationToken cancel)
    {
        string project = LocalEngine.FindProject();
        string? python = LocalEngine.LocalPython;
        string script = Path.Combine(project, "experiments", "attachment_document.py");
        if (!File.Exists(python) || !File.Exists(script))
            throw new InvalidDataException("The local document reader is missing from this Phoenix installation.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var start = new ProcessStartInfo(python!)
        {
            WorkingDirectory = project,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-I", "-B", "-X", "utf8", script, extension, ChatAttachments.MaxText.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);

        bool entered = false;
        try
        {
            await ReaderSlot.WaitAsync(token);
            entered = true;
            using var process = Process.Start(start)
                ?? throw new InvalidDataException("Phoenix couldn’t start its local document reader.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(token);
                var errors = process.StandardError.ReadToEndAsync(token);
                using (var input = process.StandardInput.BaseStream)
                    await source.CopyToAsync(input, token);
                await process.WaitForExitAsync(token);
                await errors;
                using var response = JsonDocument.Parse(await output);
                var result = response.RootElement;
                if (result.TryGetProperty("error", out var problem))
                    throw new InvalidDataException(problem.GetString() ?? "The document couldn’t be read.");
                if (process.ExitCode != 0 || !result.TryGetProperty("text", out var text) ||
                    text.GetString() is not { Length: > 0 and <= ChatAttachments.MaxText } excerpt)
                    throw new InvalidDataException("The document had no readable content.");
                string? note = result.TryGetProperty("note", out var detail) ? detail.GetString() : null;
                bool truncated = result.TryGetProperty("truncated", out var limit) && limit.GetBoolean();
                return new(name, excerpt, Truncated: truncated, Note: note);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidDataException("The document took too long to read. Try a smaller file.");
        }
        catch (Exception error) when (error is Win32Exception or JsonException or IOException)
        {
            DiagnosticLog.Write("attachment", $"Document reader failed: {error.GetType().Name}");
            throw new InvalidDataException("Phoenix couldn’t read this document. Check that it opens normally.", error);
        }
        finally { if (entered) ReaderSlot.Release(); }
    }
}
