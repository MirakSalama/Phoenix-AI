using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

public sealed partial class MainWindow
{
    private async Task RunPageRequestAsync(PageRequest request, string question)
    {
        if (request.Previous is { } previous)
        {
            Record(new("Page context", "Reused from this chat", previous.Address.Host));
            await RunLookupAsync("Chrome Browser", null, question,
                _ => Task.FromResult(PageAnswer(previous, followup: true)), reuseStatus: "Using the page from this chat…");
            return;
        }
        if (VisibleTurnServers.FirstOrDefault(IsApprovedChrome) is not { } browser)
        {
            SetReplySource("PHOENIX");
            FinishReply("Chrome Browser isn’t available to this profile. Enable the public-page Chrome MCP on the Windows host.");
            return;
        }
        var address = request.Address;
        await RunLookupAsync(browser.Name, $"I’ll read {address.Host} in Chrome.", question, async cancel =>
        {
            try
            {
                var result = await McpClient.CallResultAsync(browser, new("read_page",
                    JsonSerializer.SerializeToElement(new { url = address.AbsoluteUri })), cancel);
                if (!result.TryGetProperty("structuredContent", out var page) || page.ValueKind != JsonValueKind.Object ||
                    !page.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                    PageRequest.ReadHttpsAddress(url.GetString()) is not { } finalAddress ||
                    !page.TryGetProperty("text", out var content) || content.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(content.GetString()))
                    throw new InvalidOperationException("Chrome returned no readable page.");
                string detail = address == finalAddress ? finalAddress.Host : $"{address.Host} → {finalAddress.Host}";
                Record(new(browser.Name, "Page read", detail));
                return PageAnswer(new PageEvidence(finalAddress, LocalWorkspace.Limit(content.GetString()!, 2200)));
            }
            catch (Exception) when (!cancel.IsCancellationRequested)
            {
                Record(new(browser.Name, "Page read failed", address.Host));
                throw new InvalidOperationException($"Chrome couldn’t read {address.Host} as a public HTTPS page.");
            }
        });
    }

    private static ResearchAnswer PageAnswer(PageEvidence page, bool followup = false) => new(
        followup
            ? $"I couldn’t find a specific answer in the excerpt I saved from {page.Address.Host}. Here’s the text I have:\n\n{page.Text}"
            : $"Here’s what I could read on {page.Address.Host}:\n\n{page.Text}",
        [new Citation(page.Address.Host, page.Address)], Page: page);

    private static string[] PagePassages(PageEvidence page) =>
        Regex.Split(page.Text, @"\r?\n+|(?<=[.!?])\s+(?=[A-Z0-9])")
            .Select(passage => passage.Trim()).Where(passage => passage.Length >= 20).Take(16).ToArray();

    private static string? SelectPagePassages(PageEvidence page, string? selection)
    {
        if (selection is null || !Regex.IsMatch(selection.Trim(), @"^\d{1,2}(?:\s*,\s*\d{1,2}){0,2}\.?$"))
            return null;
        var passages = PagePassages(page);
        var indices = selection.Trim().TrimEnd('.').Split(',').Select(value => int.Parse(value.Trim())).Distinct().ToArray();
        if (indices.Any(index => index < 1 || index > passages.Length)) return null;
        return $"On {page.Address.Host}:\n\n" + string.Join("\n\n", indices.Select(index => passages[index - 1]));
    }
}
