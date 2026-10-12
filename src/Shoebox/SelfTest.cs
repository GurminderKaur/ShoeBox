namespace Shoebox;

/// <summary>Built-in checks for the parts that must be exactly right: arithmetic, tax rules, categories, parsing.</summary>
public static class SelfTest
{
    private static int _pass, _fail;

    public static int Run()
    {
        var today = new DateTime(2026, 10, 12);
        var noReg = new Settings { GstRegistered = false, VehicleBusinessPercent = 60, PhoneBusinessPercent = 50 };
        var reg = new Settings { GstRegistered = true, VehicleBusinessPercent = 60, PhoneBusinessPercent = 50 };

        // ── Parsing what the model returns ─────────────────────────────────
        var x = Reader.Parse("""Sure! {"is_receipt":true,"vendor":"Bow Valley Fuel","date":"Oct 3, 2026","subtotal":"$60.00","gst":3,"other_tax":0,"tip":0,"total":63.00,"items":"regular gas","category":"9281"}""");
        Eq("parse: tolerant of chatter, string money, month names", (x.Vendor, x.Date, x.Subtotal, x.Gst, x.Total), ("Bow Valley Fuel", "2026-10-03", 60.00m, 3m, 63.00m));
        Eq("date: ambiguous 03/04/2026 kept for review", Reader.NormalizeDate("03/04/2026"), "03/04/2026");

        // ── Categories ─────────────────────────────────────────────────────
        Eq("rule: gas station → vehicle", Categories.Choose("Petro-Canada #1234", "unleaded 40L", "8523"), (Categories.Vehicle, "rule"));
        Eq("rule: coffee → meals", Categories.Choose("Chinook Coffee House", "2 lattes", "9270"), (Categories.Meals, "rule"));
        Eq("rule: Taco Bell is food, not a phone bill", Categories.Choose("Taco Bell", "combo", "9270").Item1, Categories.Meals);
        Eq("ai fallback used when no rule matches", Categories.Choose("Mystery Co", "widget", "8960"), ("8960", "ai"));
        Eq("invalid ai code → other", Categories.Choose("Mystery Co", "widget", "1234"), (Categories.Other, "ai"));

        // ── Checks ─────────────────────────────────────────────────────────
        var good = R("Bow Valley Fuel", "2026-10-03", 60m, 3m, 0, 63m, Categories.Vehicle);
        Checker.Apply(good, Array.Empty<Receipt>(), today);
        Eq("clean Alberta receipt is OK", (good.Status, good.Checks.Count), (ReceiptStatus.Ok, 0));

        var bad = R("Bow Valley Fuel", "2026-10-03", 60m, 3m, 0, 68m, Categories.Vehicle);
        Checker.Apply(bad, Array.Empty<Receipt>(), today);
        True("misread total is flagged", bad.Status == ReceiptStatus.Review && bad.Checks.Any(c => c.Contains("don't add up")));

        var hst = R("Toronto Cafe", "2026-09-01", 100m, 13m, 0, 113m, Categories.Meals);
        Checker.Apply(hst, Array.Empty<Receipt>(), today);
        True("13% HST flagged as not Alberta GST", hst.Status == ReceiptStatus.Review && hst.Cautions.Any(c => c.Contains("Alberta GST is 5%")));

        hst.EditedByUser = true; Checker.Apply(hst, Array.Empty<Receipt>(), today);
        Eq("caution clears to OK once you confirm it", (hst.Status, hst.Cautions.Count), (ReceiptStatus.Ok, 1));
        bad.EditedByUser = true; Checker.Apply(bad, Array.Empty<Receipt>(), today);
        Eq("a real problem stays flagged even after saving", bad.Status, ReceiptStatus.Review);

        var groceries = R("Prairie Market", "2026-09-01", 50m, 1m, 0, 51m, Categories.Other);
        Checker.Apply(groceries, Array.Empty<Receipt>(), today);
        True("low GST hints at exempt items", groceries.Cautions.Any(c => c.Contains("GST-exempt")));

        var future = R("X", "2027-01-01", 10m, 0.5m, 0, 10.5m, Categories.Other);
        Checker.Apply(future, Array.Empty<Receipt>(), today);
        True("future date flagged", future.Checks.Any(c => c.Contains("future")));

        var noSub = R("Chinook Coffee House", "2026-10-01", 0m, 0.50m, 2.00m, 12.50m, Categories.Meals);
        Checker.Apply(noSub, Array.Empty<Receipt>(), today);
        Eq("missing subtotal derived from total − GST − tip", noSub.Subtotal, 10.00m);

        var dupA = R("Chinook Coffee House", "2026-10-01", 10m, 0.5m, 0, 10.5m, Categories.Meals); dupA.Id = "a"; dupA.FileName = "a.jpg"; dupA.Status = ReceiptStatus.Ok;
        var dupB = R("CHINOOK COFFEE HOUSE", "2026-10-01", 10m, 0.5m, 0, 10.5m, Categories.Meals); dupB.Id = "b";
        Checker.Apply(dupB, new[] { dupA }, today);
        True("duplicate detected (case-insensitive vendor)", dupB.Cautions.Any(c => c.Contains("duplicate")));

        // ── Deductible & ITC math ──────────────────────────────────────────
        var meal = R("Cafe", "2026-10-01", 40m, 2m, 8m, 50m, Categories.Meals); meal.Status = ReceiptStatus.Ok;
        Eq("meals, not registered: 50% of total incl. GST", Checker.Amounts(meal, noReg), (25.00m, 0m));
        Eq("meals, registered: 50% of pre-GST cost + 50% ITC", Checker.Amounts(meal, reg), (24.00m, 1.00m));
        var gas = R("Fuel", "2026-10-01", 60m, 3m, 0, 63m, Categories.Vehicle); gas.Status = ReceiptStatus.Ok;
        Eq("vehicle at 60% business use, not registered", Checker.Amounts(gas, noReg), (37.80m, 0m));
        Eq("vehicle at 60% business use, registered", Checker.Amounts(gas, reg), (36.00m, 1.80m));
        var personal = R("Grocer", "2026-10-01", 30m, 0, 0, 30m, Categories.Personal); personal.Status = ReceiptStatus.Ok;
        Eq("personal is excluded", Checker.Amounts(personal, reg), (0m, 0m));

        // ── Export ─────────────────────────────────────────────────────────
        var evil = R("=HYPERLINK(\"x\")", "2026-10-01", 1m, 0.05m, 0, 1.05m, Categories.Other); evil.Status = ReceiptStatus.Ok;
        var csv = Export.Csv(new[] { evil }, 2026, noReg);
        True("CSV neutralizes formula injection", csv.Contains("\"'=HYPERLINK(\"\"x\"\")\""));
        var sum = Export.Summarize(new[] { meal, gas, personal }, 2026, noReg);
        Eq("summary excludes personal, totals deductible", (sum.Lines.Count, sum.Deductible), (2, 62.80m));

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static Receipt R(string vendor, string date, decimal sub, decimal gst, decimal tip, decimal total, string cat) => new()
    {
        Id = Guid.NewGuid().ToString("N"), FileName = "test.jpg", Vendor = vendor, Date = date,
        Subtotal = sub, Gst = gst, Tip = tip, Total = total, Category = cat, Status = ReceiptStatus.Processing,
    };

    private static void Eq<T>(string name, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Report(name, ok, ok ? "" : $"expected {expected}, got {actual}");
    }

    private static void True(string name, bool cond) => Report(name, cond, "");

    private static void Report(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(ok ? "" : "  — " + detail)}");
    }
}
