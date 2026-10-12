using System.Globalization;
using System.Text;

namespace Shoebox;

public sealed record LineTotal(string Code, string Name, int Count, decimal Spent, decimal Gst, decimal Deductible, decimal Itc);

public sealed record Summary(int Year, int Receipts, int NeedReview, decimal Spent, decimal Deductible, decimal Itc, List<LineTotal> Lines);

public static class Export
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<Receipt> ForYear(IEnumerable<Receipt> all, int? year) =>
        all.Where(r => r.Status is ReceiptStatus.Ok or ReceiptStatus.Review)
           .Where(r => year == null || r.Date.StartsWith($"{year}-", StringComparison.Ordinal))
           .OrderBy(r => r.Date, StringComparer.Ordinal);

    public static Summary Summarize(IEnumerable<Receipt> all, int? year, Settings s)
    {
        var list = ForYear(all, year).ToList();
        var lines = list.Where(r => r.Category != Categories.Personal)
            .GroupBy(r => r.Category)
            .Select(g =>
            {
                var amounts = g.Select(r => Checker.Amounts(r, s)).ToList();
                return new LineTotal(g.Key, Categories.NameOf(g.Key), g.Count(),
                    g.Sum(r => r.Total), g.Sum(r => r.Gst), amounts.Sum(a => a.Deductible), amounts.Sum(a => a.Itc));
            })
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .ToList();
        return new Summary(year ?? 0, list.Count, list.Count(r => r.Status == ReceiptStatus.Review),
            lines.Sum(l => l.Spent), lines.Sum(l => l.Deductible), lines.Sum(l => l.Itc), lines);
    }

    /// <summary>One row per receipt; opens directly in Excel or Google Sheets, or hand it to an accountant.</summary>
    public static string Csv(IEnumerable<Receipt> all, int? year, Settings s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date,Vendor,What,T2125 line,Category,Subtotal,GST,Other tax,Tip,Total,Deductible (estimate),ITC (estimate),Status,Checks,Notes,File");
        foreach (var r in ForYear(all, year))
        {
            var (ded, itc) = Checker.Amounts(r, s);
            sb.AppendLine(string.Join(',',
                C(r.Date), C(r.Vendor), C(r.Items), C(r.Category == Categories.Personal ? "" : r.Category), C(Categories.NameOf(r.Category)),
                N(r.Subtotal), N(r.Gst), N(r.OtherTax), N(r.Tip), N(r.Total), N(ded), N(itc),
                C(r.Status == ReceiptStatus.Ok ? "OK" : "Needs review"), C(string.Join(" | ", r.Checks.Concat(r.Cautions))), C(r.Notes), C(r.FileName)));
        }
        var sum = Summarize(all, year, s);
        sb.AppendLine();
        sb.AppendLine("Summary by T2125 line,,,,,,,,,,,");
        sb.AppendLine("Line,Category,Receipts,Spent,GST paid,Deductible (estimate),ITC (estimate)");
        foreach (var l in sum.Lines)
            sb.AppendLine(string.Join(',', C(l.Code), C(l.Name), l.Count.ToString(Inv), N(l.Spent), N(l.Gst), N(l.Deductible), N(l.Itc)));
        sb.AppendLine(string.Join(',', "Total", "", sum.Lines.Sum(l => l.Count).ToString(Inv), N(sum.Spent), N(sum.Lines.Sum(l => l.Gst)), N(sum.Deductible), N(sum.Itc)));
        sb.AppendLine();
        sb.AppendLine(C($"GST registered: {(s.GstRegistered ? "yes" : "no")}; vehicle business use {s.VehicleBusinessPercent}%; phone business use {s.PhoneBusinessPercent}%. Estimates only, not tax advice. Check with CRA or an accountant."));
        return sb.ToString();
    }

    private static string N(decimal d) => d.ToString("0.00", Inv);
    private static string C(string s)
    {
        s ??= "";
        // Guard against spreadsheet formula injection from text read off a receipt.
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
