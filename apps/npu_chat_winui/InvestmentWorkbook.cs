using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed record WorkbookAnalysis(string Report, int Passes, bool Agreed, int DraftCorrections = 0);

// Applies to workbooks with a monthly baseline, an option cost table and tariff/savings scenarios.
// The brief supplies the policy and gates. A different layout or policy stays on the normal chat route.
internal static partial class InvestmentWorkbook
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const decimal Million = 1_000_000;
    private const int MaximumPasses = 32;

    internal const string WorkersFirst = "Use the workers first, then verify with the workbook calculator.";
    internal const string DraftFormat = "Draft the results using the attachments and worker notes. " +
        "Phoenix will invoke the calculator afterward. Return these compact pipe-separated rows, with no headers or prose. " +
        "Use EGP millions except for site actuals spoilage (EGP). Include 2 decimal places for money.\n" +
        "Limits | budget | readiness months | outage hours\n" +
        "Actuals | kWh | spoilage | downtime hours\n" +
        "Option A | upfront | readiness months | cutover hours | pass or fail (repeat for each option)\n" +
        "Base A | PV cost | incremental NPV | payback in years, Baseline, or Outside horizon (repeat for each scenario and option)\n" +
        "Year 0 | incremental cash flow A | incremental cash flow B | incremental cash flow C (repeat for each base-case year)\n" +
        "Sensitivity B | revised upfront | revised incremental NPV\n" +
        "Recommendation | option letter or Defer";

    internal static readonly string[] DraftParts =
    [
        "Calculate the nine scenario rows with discounted cost, incremental NPV and simple payback. Work through year-zero cost and year-end operating expense for each option.",
        "Calculate all six base-case incremental cash flow rows versus A. Check electricity charges, residual spoilage and maintenance before discounting.",
        "Check eligibility and recommendation, and recalculate option B with the higher upfront cost requested.",
        "Reconcile site actuals and extract the sponsor's limits. List values and units."
    ];

    private sealed record Sheet(string Name, SortedDictionary<int, Dictionary<string, string>> Rows)
    {
        internal int Header(string firstCell) =>
            Rows.FirstOrDefault(row => string.Equals(row.Value.GetValueOrDefault("A"), firstCell, StringComparison.OrdinalIgnoreCase)).Key;
        internal Dictionary<string, string> At(int row) => Rows.GetValueOrDefault(row) ?? throw new FormatException();
        internal int Find(string label) => Header(label) is var row && row > 0 ? row : throw new FormatException();
    }

    private sealed record Option(string Id, string Name, decimal Capex, decimal Maintenance, decimal AvoidedSpoilage,
        decimal Months, decimal Hours);
    private sealed record Scenario(string Name, decimal Tariff, decimal Growth, decimal[] EnergySavings);
    private sealed record Brief(int Years, int Page, decimal Budget, decimal Months, decimal Hours, decimal Discount);
    private sealed record Costs(decimal PresentValue, decimal[] Cash);

    internal static WorkbookAnalysis? Analyze(string request, IReadOnlyList<ChatAttachment>? files, string? draft = null)
    {
        if (files is null || !RequestedAnalysis().IsMatch(request)) return null;
        var books = files.Where(file => !file.Truncated && Path.GetExtension(file.Name).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)).ToArray();
        var briefs = files.Where(file => !file.Truncated && Path.GetExtension(file.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (books.Length != 1 || briefs.Length != 1) return null;
        try
        {
            Dictionary<string, string>? checks = draft is null ? null : new(StringComparer.OrdinalIgnoreCase);
            var report = Build(request, books[0], briefs[0], checks);
            if (report is null) return null;

            int requested = RequestedPasses(request);
            int passes = Math.Clamp(requested, 1, MaximumPasses);
            string[] corrections = draft is null ? [] : CompareDraft(draft, checks!);
            if (corrections.Length > 0) passes = Math.Max(passes, 2);
            for (int pass = 2; pass <= passes; pass++)
            {
                if (!string.Equals(report, Build(request, books[0], briefs[0]), StringComparison.Ordinal))
                    return new("The workbook calculation changed between passes, so I can’t confirm the recommendation.", pass, false);
            }

            if (passes > 1)
                report += $"\n\n**Verification.** All {passes} fresh calculation passes from the attachments agreed.";
            if (requested < 1 || requested > MaximumPasses)
                report += $"\n\nPhoenix runs between 1 and {MaximumPasses} workbook calculation passes per reply. It ran {passes} for this request.";
            if (draft is not null)
            {
                if (corrections.Length == 0)
                    report += "\n\n**Worker check.** The draft figures and recommendation matched the workbook calculation.";
                else
                    report += $"\n\n**Worker check.** Replaced {corrections.Length} mismatched or missing draft checks with figures from the attachments. " +
                        $"A fresh calculation confirmed the corrected report. Affected checks include {string.Join(", ", corrections.Take(6))}.";
            }
            return new(report, passes, true, corrections.Length);
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string[] CompareDraft(string draft, Dictionary<string, string> checks)
    {
        var rows = draft.Split('\n').Select(line => line.Trim().Trim('|').Split('|'))
            .Where(cells => cells.Length > 1)
            .GroupBy(cells => cells[0].Trim().Trim('*'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        return checks.Where(check => !rows.TryGetValue(check.Key, out var candidates) || candidates.Length != 1 ||
                !string.Equals(check.Value, DraftCells(candidates[0].Skip(1)), StringComparison.OrdinalIgnoreCase))
            .Select(check => check.Key).ToArray();
    }

    private static string DraftCells(IEnumerable<string> cells) => string.Join('|', cells.Select(raw =>
    {
        string value = raw.Trim().Trim('*');
        return decimal.TryParse(value, NumberStyles.Number, Invariant, out decimal number)
            ? number.ToString("G29", Invariant) : value;
    }));

    private static int RequestedPasses(string request)
    {
        var match = PassCount().Matches(request).LastOrDefault();
        if (match is null) return 1;
        string count = match.Groups["count"].Value.ToLowerInvariant();
        int passes = count switch
        {
            "once" or "one" => 1,
            "twice" or "two" => 2,
            "thrice" or "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            _ => int.TryParse(count, NumberStyles.None, Invariant, out int numeric) ? numeric : int.MaxValue
        };
        return match.Groups["extra"].Success && passes < int.MaxValue ? passes + 1 : passes;
    }

    private static string? Build(string request, ChatAttachment book, ChatAttachment sponsor, Dictionary<string, string>? checks = null)
    {
        var terms = ReadPolicy(sponsor.Text);
        if (terms is null) return null;
        var sheets = ReadSheets(book.Text);
        var actuals = sheets.Single(sheet => sheet.Header("Month") != 0);
        var optionsSheet = sheets.Single(sheet => sheet.Header("Input") != 0 && sheet.Header("Equipment and installation") != 0);
        var scenarioSheet = sheets.Single(sheet => sheet.Header("Scenario") != 0);

        int actualHeader = actuals.Header("Month");
        var aHeader = actuals.At(actualHeader);
        if (aHeader.GetValueOrDefault("B") != "Electricity (kWh)" ||
            aHeader.GetValueOrDefault("C") != "Spoilage (EGP)" ||
            aHeader.GetValueOrDefault("D") != "Unplanned downtime (h)") return null;
        var monthRows = actuals.Rows.Where(row => row.Key > actualHeader &&
            Month().IsMatch(row.Value.GetValueOrDefault("A") ?? "")).ToArray();
        if (monthRows.Length != 12 || monthRows.Select(row => row.Value["A"]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 12)
            return null;
        var total = new[] { "B", "C", "D" }.Select(col => monthRows.Sum(row => Number(row.Value, col))).ToArray();
        var totalRow = actuals.Rows.Single(row => row.Key > monthRows[^1].Key &&
            (row.Value.GetValueOrDefault("A") ?? "").EndsWith("total", StringComparison.OrdinalIgnoreCase));
        if (!total.SequenceEqual(new[] { "B", "C", "D" }.Select(col => Number(totalRow.Value, col)))) return null;

        int oHeader = optionsSheet.Header("Input");
        var columns = optionsSheet.At(oHeader).Where(cell => cell.Key is not ("A" or "B"))
            .Select(cell => (Column: cell.Key, Label: OptionLabel().Match(cell.Value))).ToArray();
        if (columns.Length is < 2 or > 6 || columns.Any(cell => !cell.Label.Success)) return null;
        decimal Input(string label, string unit, string column, bool percent = false)
        {
            var input = optionsSheet.At(optionsSheet.Find(label));
            if (input.GetValueOrDefault("B") != unit) throw new FormatException();
            return Number(input, column, percent);
        }
        var options = columns.Select(entry => new Option(entry.Label.Groups[1].Value, entry.Label.Groups[2].Value,
            Million * (Input("Equipment and installation", "EGPm", entry.Column) + Input("Temporary cooling", "EGPm", entry.Column)
                + Input("Contingency allowance", "EGPm", entry.Column)),
            Million * Input("Annual maintenance", "EGPm/yr", entry.Column),
            Input("Avoided spoilage share", "%", entry.Column, true),
            Input("Ready in", "months", entry.Column), Input("Longest planned cutover", "hours", entry.Column)))
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (options[0].Id != "A" || options.Select(item => item.Id).Distinct().Count() != options.Length) return null;

        int sHeader = scenarioSheet.Header("Scenario");
        var sCols = scenarioSheet.At(sHeader);
        if (sCols.GetValueOrDefault("B") != "Y1 tariff (EGP/kWh)" || sCols.GetValueOrDefault("C") != "Tariff growth")
            return null;
        var scenarios = scenarioSheet.Rows.Where(row => row.Key > sHeader && row.Value.ContainsKey("A"))
            .Select(row => new Scenario(row.Value["A"], Number(row.Value, "B"), Number(row.Value, "C", true),
                options.Select(o => Number(row.Value, sCols.Single(cell => cell.Value == $"{o.Id} kWh saved").Key, true)).ToArray()))
            .Take(9).ToArray();
        if (scenarios.Length is < 2 or > 8) return null;
        var baseScenario = scenarios.Single(item => item.Name.Equals("Base", StringComparison.OrdinalIgnoreCase));

        Costs Calculate(Option option, Scenario scenario, int index)
        {
            var cash = new decimal[terms.Years + 1];
            cash[0] = option.Capex;
            decimal tariff = scenario.Tariff;
            decimal pv = cash[0], discount = 1;
            for (int year = 1; year <= terms.Years; year++)
            {
                cash[year] = total[0] * (1 - scenario.EnergySavings[index]) * tariff +
                    total[1] * (1 - option.AvoidedSpoilage) + option.Maintenance;
                discount *= 1 + terms.Discount;
                pv += cash[year] / discount;
                tariff *= 1 + scenario.Growth;
            }
            return new(pv, cash);
        }

        var baseCosts = options.Select((option, i) => Calculate(option, baseScenario, i)).ToArray();
        string Failures(Option option) => string.Join(", ", new[]
        {
            option.Capex > terms.Budget ? "funding" : "",
            option.Months > terms.Months ? "readiness" : "",
            option.Hours > terms.Hours ? "cutover" : ""
        }.Where(value => value.Length > 0));
        var feasible = options.Select((option, index) => (Option: option, Index: index))
            .Where(entry => Failures(entry.Option).Length == 0).OrderBy(entry => baseCosts[entry.Index].PresentValue).ToArray();

        void Check(string name, params string[] values) => checks?.Add(name, DraftCells(values));
        Check("Recommendation", feasible.Length == 0 ? "Defer" : feasible[0].Option.Id);
        Check("Limits", Money(terms.Budget), terms.Months.ToString(Invariant), terms.Hours.ToString(Invariant));
        Check("Actuals", total.Select(value => value.ToString(Invariant)).ToArray());

        var report = new StringBuilder();
        if (feasible.Length == 0) report.AppendLine("**Recommendation: Defer approval.** No option passes all three project limits.");
        else
        {
            var best = feasible[0];
            report.AppendLine($"**Recommendation: Option {best.Option.Id} ({best.Option.Name}).** It passes all three project limits " +
                $"and has the lowest base-case present value cost among eligible options, **EGP {Money(baseCosts[best.Index].PresentValue)}m**.");
        }
        report.AppendLine().AppendLine($"**Source checks.** {sponsor.Name}, page {terms.Page}: EGP {Money(terms.Budget)}m budget, " +
            $"{terms.Months} months to readiness, {terms.Hours} hours maximum planned cooling outage. " +
            $"{book.Name}, {actuals.Name}!B{monthRows[0].Key}:D{totalRow.Key}: the 12 months reconcile to " +
            $"**{total[0].ToString("N0", Invariant)} kWh**, **EGP {total[1].ToString("N0", Invariant)} spoilage**, " +
            $"and **{total[2]} hours unplanned downtime**. Unplanned annual downtime is separate from the per-cutover limit.");
        report.AppendLine().AppendLine($"**Project limits** · {optionsSheet.Name}, rows {oHeader + 1}–{optionsSheet.Find("Longest planned cutover")}.");
        report.AppendLine("| Option | Upfront EGPm | Ready (months) | Cutover (hours) | Result |")
            .AppendLine("|---|---:|---:|---:|---|");
        foreach (var option in options)
        {
            Check($"Option {option.Id}", Money(option.Capex), option.Months.ToString(Invariant), option.Hours.ToString(Invariant),
                Failures(option).Length == 0 ? "Pass" : "Fail");
            report.AppendLine($"| {option.Id} · {option.Name} | {Money(option.Capex)} | {option.Months} | {option.Hours} | " +
                (Failures(option).Length == 0 ? "Pass" : "Fails " + Failures(option)) + " |");
        }

        report.AppendLine().AppendLine($"**{terms.Years}-year costs and returns** · EGP millions; discount rate {terms.Discount:P0}. " +
            $"Inputs: {optionsSheet.Name}, rows {oHeader + 1}–{optionsSheet.Find("Avoided spoilage share")}; " +
            $"{scenarioSheet.Name}, rows {sHeader + 1}–{scenarioSheet.Rows.Keys.Last()}.");
        report.AppendLine("| Scenario | Option | PV cost | Incremental NPV vs A | Simple payback vs A |")
            .AppendLine("|---|---|---:|---:|---:|");
        foreach (var scenario in scenarios)
        {
            var costs = options.Select((option, index) => Calculate(option, scenario, index)).ToArray();
            for (int index = 0; index < options.Length; index++)
            {
                var flows = costs[0].Cash.Zip(costs[index].Cash, (a, b) => a - b).ToArray();
                string payback = index == 0 ? "Baseline" : Payback(flows);
                Check($"{scenario.Name} {options[index].Id}", Money(costs[index].PresentValue),
                    Money(costs[0].PresentValue - costs[index].PresentValue), payback.Replace(" yr", "", StringComparison.Ordinal));
                report.AppendLine($"| {scenario.Name} | {options[index].Id} | {Money(costs[index].PresentValue)} | " +
                    $"{Money(costs[0].PresentValue - costs[index].PresentValue)} | {payback} |");
            }
        }

        report.AppendLine().AppendLine("**Base-case incremental cash flows vs A** · EGP millions, before discounting.");
        report.AppendLine("| Year | " + string.Join(" | ", options.Select(o => o.Id)) + " |")
            .AppendLine("|---|" + string.Concat(options.Select(_ => "---:|")));
        for (int year = 0; year <= terms.Years; year++)
        {
            var cash = baseCosts.Select(cost => Money(baseCosts[0].Cash[year] - cost.Cash[year])).ToArray();
            Check($"Year {year}", cash);
            report.AppendLine($"| {year} | {string.Join(" | ", cash)} |");
        }
        report.AppendLine($"Year zero includes equipment, temporary cooling and contingency. Operating cost is remaining kWh × tariff " +
            $"plus residual spoilage and maintenance. Tariffs escalate from year two. Incremental NPV is A's PV cost minus the option's PV cost. " +
            "Payback interpolates within the crossing year and excludes discounting.");

        var stress = Sensitivity().Match(request);
        if (stress.Success && Array.FindIndex(options, option => option.Id == stress.Groups[1].Value.ToUpperInvariant()) is >= 1 and var tested)
        {
            decimal change = decimal.Parse(stress.Groups[2].Value, Invariant) / 100;
            decimal revised = options[tested].Capex * (1 + change), gap = revised - terms.Budget;
            string revisedNpv = Money(baseCosts[0].PresentValue - baseCosts[tested].PresentValue - (revised - options[tested].Capex));
            Check($"Sensitivity {options[tested].Id}", Money(revised), revisedNpv);
            report.AppendLine().AppendLine($"**Capex sensitivity.** {options[tested].Id} at {change:P0} higher upfront costs **EGP {Money(revised)}m** " +
                $"({(gap > 0 ? $"EGP {Money(gap)}m above the cap" : $"EGP {Money(-gap)}m below the cap")}). " +
                $"Base-case incremental NPV becomes **EGP {revisedNpv}m**.");
        }

        if (feasible.Length > 0)
        {
            var best = feasible[0];
            report.AppendLine().AppendLine("**What would change the decision.** Reassess if supplier pricing or measured savings closes the PV cost gap " +
                "and the alternative passes all project limits. A positive upside NPV alone does not clear a failed gate.");
            if (feasible.Length > 1)
                report.AppendLine($"The next eligible option needs EGP {Money(baseCosts[feasible[1].Index].PresentValue - baseCosts[best.Index].PresentValue)}m " +
                    "more in present-value benefit or lower cost to tie the base case.");
        }
        report.AppendLine().AppendLine("**Delivery and risk ownership** · Proposed sequencing, subject to site approval.");
        report.AppendLine("- Before mobilization: Finance confirms supplier pricing, contingency and the investment model. Operations signs off on continuity and temporary cooling capacity.");
        report.AppendLine("- Before each planned outage: Facilities tests rollback and temperature recovery, including the one-room controls pilot if controls change. Quality approves stock isolation and release criteria.");
        report.AppendLine($"- At cutover: Operations owns the go/no-go. If recovery is slipping or the outage could approach {terms.Hours} hours, stop, restore the previous configuration or switch to tested temporary cooling, and quarantine affected stock for Quality review.");
        report.AppendLine("- Review progress weekly. Commission and hand over within the chosen option's readiness plan. Monitor downtime, energy use and spoilage after handover.");
        return report.ToString().TrimEnd();
    }

    private static Brief? ReadPolicy(string text)
    {
        foreach (var page in PdfPage().Matches(text).Cast<Match>())
        {
            var body = page.Groups[2].Value;
            var budget = Budget().Match(body);
            var months = Readiness().Match(body);
            var hours = Outage().Match(body);
            var discount = Discount().Match(body);
            var years = Years().Match(body);
            if (!budget.Success || !months.Success || !hours.Success || !discount.Success || !years.Success ||
                !Policy().IsMatch(body)) continue;
            int horizon = years.Groups[1].Value.ToLowerInvariant() switch { "five" => 5, _ => int.Parse(years.Groups[1].Value, Invariant) };
            if (horizon is < 1 or > 15) continue;
            return new(horizon, int.Parse(page.Groups[1].Value, Invariant), Parse(budget) * Million, Parse(months), Parse(hours), Parse(discount) / 100);
        }
        return null;
    }

    private static decimal Parse(Match value) => decimal.Parse(value.Groups[1].Value, Invariant);
    private static decimal Number(Dictionary<string, string> row, string column, bool percent = false)
    {
        string value = row.GetValueOrDefault(column)?.Trim() ?? throw new FormatException();
        if (percent && !value.EndsWith('%')) throw new FormatException();
        if (!decimal.TryParse(value.TrimEnd('%'), NumberStyles.Float, Invariant, out decimal result) ||
            result < 0 || (percent ? result > 100 : result > 1_000_000_000) || (!percent && value.EndsWith('%')))
            throw new FormatException();
        return percent ? result / 100 : result;
    }

    private static string Money(decimal number) => (number / Million).ToString("0.00", Invariant);

    private static string Payback(decimal[] flows)
    {
        decimal cumulative = flows[0];
        if (cumulative >= 0) return "Immediate";
        for (int year = 1; year < flows.Length; year++)
        {
            if (flows[year] > 0 && cumulative + flows[year] >= 0)
                return (year - 1 - cumulative / flows[year]).ToString("0.00", Invariant) + " yr";
            cumulative += flows[year];
        }
        return "Outside horizon";
    }

    private static List<Sheet> ReadSheets(string text)
    {
        var parsed = new List<Sheet>();
        Sheet? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("Worksheet: ", StringComparison.Ordinal))
            {
                current = new(line[11..], []);
                parsed.Add(current);
            }
            else if (current is not null)
            {
                foreach (string field in line.Split(" | ", StringSplitOptions.RemoveEmptyEntries))
                {
                    var cell = Cell().Match(field);
                    if (!cell.Success) continue;
                    int index = int.Parse(cell.Groups[2].Value, Invariant);
                    if (!current.Rows.TryGetValue(index, out var row)) current.Rows.Add(index, row = []);
                    if (!row.TryAdd(cell.Groups[1].Value, cell.Groups[3].Value.Trim())) throw new FormatException();
                }
            }
        }
        return parsed;
    }

    [GeneratedRegex(@"\b(?:npv|present value)\b[\s\S]*\bpayback\b|\bpayback\b[\s\S]*\b(?:npv|present value)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequestedAnalysis();
    [GeneratedRegex(@"(?i)(?<!don't )(?<!don’t )(?<!not )\b(?:recalculate|calculate|recompute|rerun|repeat|verify|check)\b[^.!?;,\r\n]{0,80}?\b(?:(?<count>once|twice|thrice)|(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:(?<extra>more|additional)\s+)?times?)\b|\b(?:run|do|perform)\s+(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:(?<extra>more|additional)\s+)?(?:fresh\s+)?(?:calculation\s+passes?|recalculations)\b")]
    private static partial Regex PassCount();
    [GeneratedRegex(@"^Page (\d+) of \d+\s*\n([\s\S]*?)(?=^Page \d+ of \d+|\z)", RegexOptions.Multiline)]
    private static partial Regex PdfPage();
    [GeneratedRegex(@"(?i)\byear-zero spend at EGP\s+(\d+(?:\.\d+)?)m\b")]
    private static partial Regex Budget();
    [GeneratedRegex(@"(?i)\breadiness is within\s+(\d+(?:\.\d+)?)\s+months\b")]
    private static partial Regex Readiness();
    [GeneratedRegex(@"(?i)\bcutover may interrupt cooling for more than\s+(\d+(?:\.\d+)?)\s+(?:consecutive\s+)?hours\b")]
    private static partial Regex Outage();
    [GeneratedRegex(@"(?i)\buse a\s+(\d+(?:\.\d+)?)%\s+nominal discount rate\b")]
    private static partial Regex Discount();
    [GeneratedRegex(@"(?i)\bcompare\s+(five|\d{1,2})\s+operating years\b")]
    private static partial Regex Years();
    [GeneratedRegex(@"(?is)\byear-zero capex\s+and year-end operating flows.*?exclude financing, tax and salvage value.*?tariff growth from year two.*?maintenance and spoilage costs flat")]
    private static partial Regex Policy();
    [GeneratedRegex(@"(?i)\boption\s+([A-Z])\b[^.!?\r\n]{0,25}?(\d{1,3})%\s+(?:more|higher)\b")]
    private static partial Regex Sensitivity();
    [GeneratedRegex(@"^([A-Z])\s*[·:–-]\s*(.{1,50})$")]
    private static partial Regex OptionLabel();
    [GeneratedRegex(@"^([A-Z]{1,3})(\d{1,7})=(.*)$")]
    private static partial Regex Cell();
    [GeneratedRegex(@"^(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)$", RegexOptions.IgnoreCase)]
    private static partial Regex Month();
}
