using System.Globalization;

namespace Shoebox;

/// <summary>
/// Plain arithmetic checks on what the model read. The model can misread a digit;
/// these rules catch it so the user knows exactly which receipts to look at.
/// </summary>
public static class Checker
{
    public const decimal AlbertaGstRate = 0.05m;

    public static void Apply(Receipt r, IEnumerable<Receipt> others, DateTime today)
    {
        r.Checks.Clear();
        r.Cautions.Clear();

        if (r.Raw is { IsReceipt: false })
            r.Checks.Add("This doesn't look like a receipt or invoice.");

        if (r.Total <= 0)
            r.Checks.Add("Couldn't read a total.");

        // Fill in a missing subtotal from the other amounts.
        if (r.Subtotal <= 0 && r.Total > 0)
            r.Subtotal = Math.Max(0, r.Total - r.Gst - r.OtherTax - r.Tip);

        if (r.Total > 0)
        {
            var sum = r.Subtotal + r.Gst + r.OtherTax + r.Tip;
            var diff = r.Total - sum;
            if (Math.Abs(diff) > 0.02m)
                r.Checks.Add($"Amounts don't add up: subtotal + tax + tip = {Money(sum)}, but total is {Money(r.Total)} (off by {Money(Math.Abs(diff))}).");
        }

        if (r.Subtotal > 0 && r.Gst > 0)
        {
            var rate = r.Gst / r.Subtotal;
            if (rate > 0.055m)
                r.Cautions.Add($"GST is {rate:P1} of the subtotal. Alberta GST is 5%. If this is an HST receipt from another province, enter only the GST/HST amount shown and check it.");
            else if (rate < 0.045m)
                r.Cautions.Add($"GST is only {rate:P1} of the subtotal. Some items may be GST-exempt (like basic groceries), or a number was misread.");
        }

        if (r.OtherTax > 0)
            r.Cautions.Add("Has PST or another provincial tax (out-of-province purchase?). Only GST/HST can be claimed as an input tax credit.");

        if (string.IsNullOrWhiteSpace(r.Date))
            r.Checks.Add("No date found.");
        else if (!DateTime.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            r.Checks.Add($"Date \"{r.Date}\" isn't a valid date (expected yyyy-mm-dd).");
        else if (d.Date > today.Date)
            r.Checks.Add("Date is in the future — probably misread.");
        else if (d.Year < today.Year - 7)
            r.Checks.Add("Date is more than 7 years old — probably misread (CRA asks you to keep records 6 years).");

        if (string.IsNullOrWhiteSpace(r.Vendor))
            r.Checks.Add("No vendor name found.");

        var dup = others.FirstOrDefault(o => o.Id != r.Id && o.Status != ReceiptStatus.Error && o.Status != ReceiptStatus.Processing
            && o.Total == r.Total && r.Total > 0 && o.Date == r.Date
            && string.Equals(Norm(o.Vendor), Norm(r.Vendor), StringComparison.Ordinal));
        if (dup != null)
            r.Cautions.Add($"Possible duplicate of \"{dup.FileName}\" (same vendor, date and total).");

        // Problems always need a look. Cautions need a look until you've checked and saved the receipt yourself.
        r.Status = r.Checks.Count > 0 || (r.Cautions.Count > 0 && !r.EditedByUser) ? ReceiptStatus.Review : ReceiptStatus.Ok;
    }

    /// <summary>
    /// Deductible expense and input tax credit (ITC) for one receipt.
    /// GST registrants deduct the cost before GST and claim the GST back as an ITC;
    /// non-registrants can't claim ITCs, so the GST is part of their expense.
    /// The same share limit (50% meals, business-use % for vehicle/phone) applies to both.
    /// </summary>
    public static (decimal Deductible, decimal Itc) Amounts(Receipt r, Settings s)
    {
        if (r.Status is ReceiptStatus.Error or ReceiptStatus.Processing) return (0, 0);
        var share = Categories.DeductibleShare(r.Category, s);
        if (share == 0) return (0, 0);
        if (s.GstRegistered)
        {
            var net = r.Total - r.Gst;
            return (Round(net * share), Round(r.Gst * share));
        }
        return (Round(r.Total * share), 0);
    }

    public static string Money(decimal m) => m.ToString("$#,##0.00", CultureInfo.InvariantCulture);
    private static decimal Round(decimal m) => Math.Round(m, 2, MidpointRounding.AwayFromZero);
    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
