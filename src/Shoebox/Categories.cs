using System.Text.RegularExpressions;

namespace Shoebox;

public sealed record Category(string Code, string Name, string Hint);

/// <summary>
/// CRA Form T2125 (Statement of Business or Professional Activities) expense lines,
/// plus "personal" for things that aren't business expenses.
/// </summary>
public static class Categories
{
    public const string Meals = "8523";
    public const string Vehicle = "9281";
    public const string Phone = "9220";
    public const string Other = "9270";
    public const string Personal = "personal";

    public static readonly IReadOnlyList<Category> All = new List<Category>
    {
        new("8521", "Advertising", "ads, flyers, business cards, website ads"),
        new(Meals, "Meals and entertainment (50%)", "restaurants, coffee, client meals"),
        new("8690", "Insurance", "business insurance (not vehicle or life)"),
        new("8710", "Interest and bank charges", "bank fees, card fees, loan interest"),
        new("8760", "Business taxes, licences and memberships", "business licence, permits, professional dues"),
        new("8810", "Office expenses", "postage, small office consumables"),
        new("8811", "Office stationery and supplies", "paper, ink, pens, printer supplies"),
        new("8860", "Professional fees", "accountant, lawyer, bookkeeping"),
        new("8910", "Rent", "rent for business space"),
        new("8960", "Repairs and maintenance", "repairs to business equipment or premises"),
        new("9200", "Travel expenses", "flights, hotels, transit, parking on trips"),
        new(Phone, "Telephone and utilities", "phone plan, internet, utilities"),
        new("9224", "Fuel costs (except motor vehicles)", "fuel for equipment, not your car"),
        new("9275", "Delivery, freight and express", "courier, shipping"),
        new(Vehicle, "Motor vehicle expenses", "gas, oil change, car wash, car repairs, parking, registration"),
        new(Other, "Other expenses", "software, tools and anything that fits nowhere else"),
        new(Personal, "Personal — not a business expense", "excluded from totals"),
    };

    public static readonly IReadOnlySet<string> Codes = All.Select(c => c.Code).ToHashSet();

    public static string NameOf(string code) => All.FirstOrDefault(c => c.Code == code)?.Name ?? "Other expenses";

    /// <summary>Share of the cost that is deductible for this category.</summary>
    public static decimal DeductibleShare(string code, Settings s) => code switch
    {
        Personal => 0m,
        Meals => 0.5m,                                      // CRA: generally 50% of meals & entertainment
        Vehicle => Math.Clamp(s.VehicleBusinessPercent, 0, 100) / 100m,
        Phone => Math.Clamp(s.PhoneBusinessPercent, 0, 100) / 100m,
        _ => 1m,
    };

    // Deterministic rules run before the AI's suggestion: predictable, explainable, and easy to extend.
    private static readonly (Regex Pattern, string Code)[] Rules =
    {
        (Rx(@"\b(petro[- ]?canada|shell|esso|husky|co-?op gas|chevron|mobil|domo|centex|fas gas|costco gas|gas bar|fuel|gasoline|unleaded|diesel|car ?wash|jiffy lube|mr\.? lube|oil change|auto(motive)?|mechanic|parking|impark|calgary parking|registry)\b"), Vehicle),
        (Rx(@"\b(tim hortons|starbucks|mcdonald'?s|a&w|subway|second cup|blenz|restaurant|bistro|cafe|café|coffee|pizza|grill|diner|pub|kitchen|sushi|bakery|eatery|dining|taco|burger|wendy'?s|noodle|earls|denny'?s|boston pizza|doordash|skip ?the ?dishes|uber ?eats)\b"), Meals),
        (Rx(@"\b(staples|office depot|grand & toy|printer|toner|ink|stationery|paper)\b"), "8811"),
        (Rx(@"\b(canada post|purolator|fedex|ups|dhl|courier|shipping|freight|postage)\b"), "9275"),
        (Rx(@"\b(telus|rogers|bell|shaw|freedom mobile|fido|koodo|virgin plus|public mobile|chatr|lucky mobile|internet|wireless|mobility|enmax|atco|epcor|direct energy)\b"), Phone),
        (Rx(@"\b(accountant|accounting|cpa|bookkeep\w*|law office|lawyer|legal|notary)\b"), "8860"),
        (Rx(@"\b(westjet|air canada|flair|swoop|hotel|inn|motel|airbnb|calgary transit|edmonton transit|ets|ctrain|airport)\b"), "9200"),
        (Rx(@"\b(insurance|insurer)\b"), "8690"),
        (Rx(@"\b(vistaprint|business cards|advertis\w*|facebook ads|google ads|kijiji featured)\b"), "8521"),
        (Rx(@"\b(licen[cs]e|permit|membership|association dues|union dues)\b"), "8760"),
        (Rx(@"\b(bank fee|service charge|interest charge|monthly fee)\b"), "8710"),
    };

    private static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Pick a category: keyword rule first, then the model's suggestion, then "Other".</summary>
    public static (string Code, string Source) Choose(string vendor, string items, string aiSuggestion)
    {
        var text = $"{vendor} {items}";
        foreach (var (pattern, code) in Rules)
            if (pattern.IsMatch(text)) return (code, "rule");
        var ai = (aiSuggestion ?? "").Trim().ToLowerInvariant();
        if (Codes.Contains(ai)) return (ai, "ai");
        return (Other, "ai");
    }
}
