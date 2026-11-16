using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NpuChat;

internal static partial class ConversationSpeech
{
    [GeneratedRegex(@"[.!?]+[”’""')\]]*(?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\b(?:mr|mrs|ms|dr|prof|sr|jr|st|vs|e\.g|i\.e|(?:[a-z]\.)+[a-z])\.$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Abbreviation();

    private static IEnumerable<int> SentenceEnds(string text, int offset)
    {
        for (var match = SentenceEnd().Match(text, offset); match.Success; match = match.NextMatch())
        {
            int end = match.Index + match.Length;
            if (!Abbreviation().IsMatch(text[Math.Max(offset, end - 24)..end])) yield return end;
        }
    }

    internal static int NextSentenceEnd(string text, int offset) => SentenceEnds(text, offset).FirstOrDefault(text.Length);

    internal static string MarkCutoff(string text, string? cutoff) => cutoff is null ? text :
        string.IsNullOrWhiteSpace(text) ? cutoff : $"{text}\n\n{cutoff}";

    internal static string ForVoice(string text, bool cutOff)
    {
        string prose = CloudResearch.TextForSpeech(text);
        if (!cutOff || prose.Length == 0) return prose;
        int end = SentenceEnds(prose, 0).LastOrDefault();
        return end == 0 ? "That reply was cut short." : prose[..end];
    }
}

internal sealed record ReplySnippet(string Language, string Code);
internal sealed record PresentedReply(string Text, IReadOnlyList<ReplySnippet> Snippets);

internal static partial class ReplyPresentation
{
    internal sealed record CodeBlock(int Start, int End, ReplySnippet Snippet);

    [GeneratedRegex(@"(?m)^[ \t]{0,3}(?<marker>\x60{3,}|~{3,})(?<info>[^\r\n]*)\r?$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"\x60+[^\x60\r\n]*\x60+|!?\[[^\]\r\n]*\]\((?:[^()\r\n]|\([^()\r\n]*\))*\)|<?https?://[^\s<>]+>?",
        RegexOptions.IgnoreCase)]
    private static partial Regex NonSpeech();

    [GeneratedRegex(@"(?m)^[ \t]*(?:>[ \t]*)*(?:#{1,6}(?:[ \t]+|$)|[-*+][ \t]+|\d+[.)][ \t]+)?")]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"(?m)^[ \t]*(?:[-*_][ \t]*){3,}$")]
    private static partial Regex Divider();

    [GeneratedRegex(@"[*#""“”„‟\x60]|(?<!\w)['‘’]|['‘’](?!\w)|(?<!\w)_+|_+(?!\w)|~~")]
    private static partial Regex Markup();

    [GeneratedRegex(@"[ \t]{2,}|[ \t]+(?=[,.!?;:])")]
    private static partial Regex ExtraSpacing();

    internal static IEnumerable<CodeBlock> CodeBlocks(string text)
    {
        Match opening = Fence().Match(text);
        while (opening.Success)
        {
            string marker = opening.Groups["marker"].Value;
            string info = opening.Groups["info"].Value.Trim();
            int bodyStart = opening.Index + opening.Length;
            if (bodyStart < text.Length && text[bodyStart] == '\n') bodyStart++;
            Match closing = opening.NextMatch();
            while (closing.Success && (closing.Groups["marker"].Value[0] != marker[0] ||
                closing.Groups["marker"].Length < marker.Length || !string.IsNullOrWhiteSpace(closing.Groups["info"].Value)))
                closing = closing.NextMatch();
            int bodyEnd = closing.Success ? closing.Index : text.Length;
            int end = closing.Success ? closing.Index + closing.Length : text.Length;
            string language = info.Split([' ', '\t'], 2)[0];
            yield return new(opening.Index, end,
                new(language, text[bodyStart..bodyEnd].TrimEnd('\r', '\n')));
            opening = closing.Success ? closing.NextMatch() : closing;
        }
    }

    internal static string MapProse(string text, Func<string, string> map, bool keepCode)
    {
        var result = new StringBuilder();
        int start = 0;
        foreach (var block in CodeBlocks(text))
        {
            result.Append(map(text[start..block.Start]));
            if (keepCode) result.Append(text[block.Start..block.End]);
            start = block.End;
        }
        return result.Append(map(text[start..])).ToString();
    }

    internal static PresentedReply Split(string text) =>
        new(MapProse(text, part => part, keepCode: false).Trim(),
            CodeBlocks(text).Select(block => block.Snippet).Where(snippet => snippet.Code.Length > 0).ToArray());

    internal static string Speech(string text)
    {
        string prose = WebUtility.HtmlDecode(MapProse(text, part => part, keepCode: false));
        prose = NonSpeech().Replace(prose, "");
        prose = Divider().Replace(prose, "");
        prose = LinePrefix().Replace(prose, "");
        prose = Markup().Replace(prose, "");
        return string.Join("\n", prose.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => ExtraSpacing().Replace(line, match => match.Index + match.Length < line.Length &&
                ",.!?;:".Contains(line[match.Index + match.Length]) ? "" : " "))
            .Where(line => line.Any(char.IsLetterOrDigit)));
    }
}
