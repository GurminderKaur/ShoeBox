using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shoebox;

/// <summary>
/// Reads a receipt image with a local open-weight vision model served by Ollama
/// (default: Google's Gemma 3, 4B). The image never leaves this computer.
/// Uses Ollama "structured outputs" so the reply is always JSON in our schema.
/// </summary>
public sealed class Reader
{
    private readonly HttpClient _http;
    public string Model { get; }
    public string BaseUrl { get; }

    public Reader(string model, string baseUrl)
    {
        Model = model;
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>null when ready; otherwise a plain-language reason.</summary>
    public async Task<string?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("/api/tags"));
            var names = doc.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "").ToList();
            bool ok = names.Any(n => n == Model || n == Model + ":latest" || (!Model.Contains(':') && n.StartsWith(Model + ":")));
            return ok ? null : $"Ollama is running, but the model \"{Model}\" isn't downloaded yet. Run: ollama pull {Model}";
        }
        catch
        {
            return "Ollama isn't running. Start the Ollama app (or run 'ollama serve'), then refresh.";
        }
    }

    private const string Prompt = """
        You are reading a photo or scan of a receipt or invoice for a self-employed person in Alberta, Canada, who needs it for their taxes.
        Extract exactly what is printed. Do not guess or invent numbers.
        - vendor: the business name at the top.
        - date: the purchase date as YYYY-MM-DD. Canadian receipts may print dates as DD/MM/YYYY or MM/DD/YYYY; use the month name if shown. Use "" if there is no date.
        - subtotal: amount before tax (0 if not printed).
        - gst: the GST amount (Alberta GST is 5%). If the receipt shows HST, put the HST amount here. 0 if no GST/HST is shown.
        - other_tax: PST/QST or any other tax, 0 if none.
        - tip: tip or gratuity, 0 if none.
        - total: the final amount paid.
        - items: a few words describing what was bought (e.g. "regular gasoline 42 L", "2 coffees and a muffin").
        - category: the best-fitting CRA T2125 expense line code from the list.
        - is_receipt: false if the image is not a receipt or invoice.
        Category codes: 8521 advertising; 8523 meals and entertainment; 8690 insurance; 8710 bank charges/interest; 8760 business licences/memberships;
        8810 office expenses; 8811 stationery and office supplies; 8860 professional fees (accounting/legal); 8910 rent; 8960 repairs and maintenance;
        9200 travel (flights, hotels, transit); 9220 phone, internet and utilities; 9224 fuel for equipment (not vehicles); 9275 delivery and shipping;
        9281 motor vehicle (gas, car repairs, car wash, parking); 9270 other business expenses (software, tools); personal (groceries, clothing, personal items).
        """;

    private static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "is_receipt": { "type": "boolean" },
            "vendor":     { "type": "string" },
            "date":       { "type": "string" },
            "subtotal":   { "type": "number" },
            "gst":        { "type": "number" },
            "other_tax":  { "type": "number" },
            "tip":        { "type": "number" },
            "total":      { "type": "number" },
            "items":      { "type": "string" },
            "category":   { "type": "string", "enum": ["8521","8523","8690","8710","8760","8810","8811","8860","8910","8960","9200","9220","9224","9275","9281","9270","personal"] }
          },
          "required": ["is_receipt","vendor","date","subtotal","gst","other_tax","tip","total","items","category"]
        }
        """)!;

    public async Task<(Extraction Result, double Seconds)> ReadAsync(byte[] image, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = false,
            ["format"] = Schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = 4096 },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = Prompt,
                    ["images"] = new JsonArray(Convert.ToBase64String(image)),
                },
            },
        };

        var sw = Stopwatch.StartNew();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/chat", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned {(int)resp.StatusCode}: {Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        var json = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}";
        return (Parse(json), sw.Elapsed.TotalSeconds);
    }

    /// <summary>Parse the model's JSON leniently (numbers may arrive as strings like "$12.50").</summary>
    public static Extraction Parse(string json)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model didn't return JSON.");
        using var doc = JsonDocument.Parse(json[start..(end + 1)]);
        var e = doc.RootElement;

        string Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        decimal Num(string k)
        {
            if (!e.TryGetProperty(k, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return Math.Round(Math.Abs(d), 2);
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = new string(v.GetString()!.Where(c => char.IsDigit(c) || c == '.').ToArray());
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d2))
                    return Math.Round(d2, 2);
            }
            return 0;
        }

        return new Extraction
        {
            IsReceipt = !e.TryGetProperty("is_receipt", out var ir) || ir.ValueKind != JsonValueKind.False,
            Vendor = Str("vendor"),
            Date = NormalizeDate(Str("date")),
            Subtotal = Num("subtotal"),
            Gst = Num("gst"),
            OtherTax = Num("other_tax"),
            Tip = Num("tip"),
            Total = Num("total"),
            Items = Str("items"),
            Category = Str("category"),
        };
    }

    /// <summary>Accept a few common shapes and return yyyy-MM-dd (or the original text if unclear).</summary>
    public static string NormalizeDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy.MM.dd", "MMM d, yyyy", "MMMM d, yyyy", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "dd-MMM-yyyy", "dd-MMM-yy" };
        if (DateTime.TryParseExact(s.Trim(), formats, inv, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var d))
            return d.ToString("yyyy-MM-dd", inv);
        return s.Trim(); // ambiguous (e.g. 03/04/2026) — keep as-is so the checker flags it for review
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
