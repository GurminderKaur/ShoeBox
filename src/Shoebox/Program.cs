using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Shoebox;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var opt = Options.Parse(args);
if (opt.Has("help")) { Options.Help(); return 0; }
if (opt.Command == "selftest") return SelfTest.Run();

var store = new Store(opt.DataDir);
var reader = new Reader(opt.Model, opt.Ollama);

if (opt.Command == "scan") return await Cli.Scan(opt, store, reader);
if (opt.Command is not (null or "serve")) { Options.Help(); return 1; }

// ── Local web app ───────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{opt.Port}");   // localhost only: nothing is exposed to the network
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 200L * 1024 * 1024);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(reader);
builder.Services.AddSingleton<Processor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Processor>());
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

// Only answer requests addressed to this machine, and require a custom header on changes,
// so a random website open in the same browser can't read or change your receipts.
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
    if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-Shoebox"] != "1") { ctx.Response.StatusCode = 403; return; }
    await next();
});

var page = ReadEmbedded("index.html");
app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));

app.MapGet("/api/status", async () =>
{
    var problem = await reader.CheckAsync();
    return new { ready = problem == null, problem, model = reader.Model, dataDir = store.Dir, version = "0.1.0" };
});

app.MapGet("/api/categories", () => Categories.All);

app.MapGet("/api/receipts", () =>
{
    var s = store.Settings;
    return store.All().OrderByDescending(r => r.Date).ThenByDescending(r => r.AddedUtc)
        .Select(r => new { receipt = r, amounts = Checker.Amounts(r, s) is var a ? new { deductible = a.Deductible, itc = a.Itc } : null });
});

app.MapPost("/api/receipts", async (HttpRequest req, Processor proc) =>
{
    var form = await req.ReadFormAsync();
    var added = new List<Receipt>();
    var rejected = new List<string>();
    foreach (var f in form.Files)
    {
        var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png"))
        {
            rejected.Add($"{f.FileName}: only JPG and PNG photos are supported for now. On iPhone, share the photo as JPG, or take a screenshot of a PDF.");
            continue;
        }
        if (f.Length > 25 * 1024 * 1024) { rejected.Add($"{f.FileName}: larger than 25 MB."); continue; }
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms);
        var r = store.Add(f.FileName, ms.ToArray());
        proc.Enqueue(r.Id);
        added.Add(r);
    }
    return Results.Ok(new { added = added.Count, rejected });
});

app.MapPut("/api/receipts/{id}", (string id, Receipt edit) =>
{
    var r = store.Get(id);
    if (r == null) return Results.NotFound();
    if (!Categories.Codes.Contains(edit.Category)) return Results.BadRequest("Unknown category.");
    if (r.Category != edit.Category) r.CategorySource = "you";
    r.Vendor = edit.Vendor.Trim();
    r.Date = Reader.NormalizeDate(edit.Date);
    r.Subtotal = Math.Abs(edit.Subtotal); r.Gst = Math.Abs(edit.Gst); r.OtherTax = Math.Abs(edit.OtherTax);
    r.Tip = Math.Abs(edit.Tip); r.Total = Math.Abs(edit.Total);
    r.Items = edit.Items.Trim(); r.Category = edit.Category; r.Notes = edit.Notes.Trim();
    r.EditedByUser = true;
    Checker.Apply(r, store.All(), DateTime.Today);
    store.Update(r);
    return Results.Ok(r);
});

app.MapPost("/api/receipts/{id}/retry", (string id, Processor proc) =>
{
    var r = store.Get(id);
    if (r == null) return Results.NotFound();
    r.Status = ReceiptStatus.Processing; r.Error = null;
    store.Update(r);
    proc.Enqueue(id);
    return Results.Ok();
});

app.MapDelete("/api/receipts/{id}", (string id) => store.Delete(id) ? Results.Ok() : Results.NotFound());

app.MapGet("/api/receipts/{id}/image", (string id) =>
{
    var r = store.Get(id);
    if (r == null) return Results.NotFound();
    var path = Path.Combine(store.FilesDir, r.StoredFile);
    var type = r.StoredFile.EndsWith(".png") ? "image/png" : "image/jpeg";
    return File.Exists(path) ? Results.File(path, type) : Results.NotFound();
});

app.MapGet("/api/settings", () => store.Settings);
app.MapPut("/api/settings", (Settings s) =>
{
    s.VehicleBusinessPercent = Math.Clamp(s.VehicleBusinessPercent, 0, 100);
    s.PhoneBusinessPercent = Math.Clamp(s.PhoneBusinessPercent, 0, 100);
    store.SaveSettings(s);
    return Results.Ok(s);
});

app.MapGet("/api/summary", (int? year) => Export.Summarize(store.All(), year, store.Settings));

app.MapGet("/api/export.csv", (int? year) =>
{
    var csv = Export.Csv(store.All(), year, store.Settings);
    var name = year is null ? "shoebox-all.csv" : $"shoebox-{year}.csv";
    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", name);
});

var url = $"http://127.0.0.1:{opt.Port}";
Console.WriteLine($"""

  Shoebox is running at {url}
  Your receipts are stored in: {store.Dir}
  Reading receipts with: {reader.Model} (local, via Ollama)
  Press Ctrl+C to stop.

""");
if (!opt.Has("no-browser")) OpenBrowser(url);
await app.RunAsync();
return 0;

static string ReadEmbedded(string name)
{
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
    using var r = new StreamReader(s);
    return r.ReadToEnd();
}

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* headless: just print the URL */ }
}

namespace Shoebox
{
    internal sealed class Options
    {
        public string? Command { get; private set; }
        public List<string> Positional { get; } = new();
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "help", "no-browser" };

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--"))
                {
                    var k = a[2..];
                    string? v = null;
                    if (k.Contains('=')) { v = k[(k.IndexOf('=') + 1)..]; k = k[..k.IndexOf('=')]; }
                    else if (!Flags.Contains(k) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) v = args[++i];
                    o._named[k] = v;
                }
                else if (o.Command == null) o.Command = a.ToLowerInvariant();
                else o.Positional.Add(a);
            }
            return o;
        }

        public bool Has(string k) => _named.ContainsKey(k);
        public string? Get(string k) => _named.TryGetValue(k, out var v) ? v : null;
        private string? GetOrEnv(string k, string env) => Get(k) ?? Environment.GetEnvironmentVariable(env);

        public int Port => int.TryParse(Get("port"), out var p) ? p : 5178;
        public string Model => GetOrEnv("model", "SHOEBOX_MODEL") ?? "gemma3:4b";
        public string Ollama => GetOrEnv("ollama", "OLLAMA_HOST") is { } h ? (h.StartsWith("http") ? h : "http://" + h) : "http://localhost:11434";
        public string DataDir => GetOrEnv("data", "SHOEBOX_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Shoebox");

        public static void Help() => Console.WriteLine("""
            Shoebox — sort receipts for your taxes, privately, on your own computer.

              shoebox                      Start the app and open it in your browser (http://127.0.0.1:5178)
              shoebox scan <folder>        Read every JPG/PNG in a folder and print a table (no browser)
              shoebox selftest             Run the built-in checks

            Options
              --model gemma3:4b            Any Ollama vision model (env SHOEBOX_MODEL), e.g. gemma3:12b, qwen2.5vl:7b
              --data <folder>              Where receipts are kept (default: ~/Shoebox, env SHOEBOX_DATA)
              --port 5178                  Local port
              --ollama http://localhost:11434
              --year 2026                  (scan) year for the summary / CSV
              --csv out.csv                (scan) also write the CSV export
              --no-browser                 Don't open the browser automatically

            Estimates only, not tax advice.
            """);
    }

    internal static class Cli
    {
        /// <summary>Headless: add every image in a folder, read it, print results. Used by CI and power users.</summary>
        public static async Task<int> Scan(Options o, Store store, Reader reader)
        {
            var dir = o.Positional.FirstOrDefault();
            if (dir == null) { Console.Error.WriteLine("Usage: shoebox scan <folder>"); return 1; }
            dir = Path.GetFullPath(dir);
            if (!Directory.Exists(dir)) { Console.Error.WriteLine($"Folder not found: {dir}"); return 1; }
            var problem = await reader.CheckAsync();
            if (problem != null) { Console.Error.WriteLine(problem); return 2; }

            var proc = new Processor(store, reader);
            var files = Directory.EnumerateFiles(dir)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
                .OrderBy(f => f).ToList();
            Console.WriteLine($"Reading {files.Count} receipt(s) with {reader.Model} (local)…\n");
            foreach (var f in files)
            {
                var r = store.Add(f, await File.ReadAllBytesAsync(f));
                await proc.ProcessAsync(r);
                r = store.Get(r.Id)!;
                var (ded, _) = Checker.Amounts(r, store.Settings);
                Console.WriteLine($"{Path.GetFileName(f),-28} {r.Status,-7} {r.Date,-10} {Trunc(r.Vendor, 24),-24} {Checker.Money(r.Total),10}  GST {Checker.Money(r.Gst),8}  {r.Category,-8} {Trunc(Categories.NameOf(r.Category), 28),-28} ded {Checker.Money(ded),9}  ({r.Seconds}s)");
                foreach (var c in r.Checks) Console.WriteLine($"{"",30}⚠ {c}");
                foreach (var c in r.Cautions) Console.WriteLine($"{"",30}ℹ {c}");
                if (r.Error != null) Console.WriteLine($"{"",30}✖ {r.Error}");
            }
            int? year = int.TryParse(o.Get("year"), out var y) ? y : null;
            var sum = Export.Summarize(store.All(), year, store.Settings);
            Console.WriteLine($"\nSummary{(year is null ? "" : $" for {year}")}: {sum.Receipts} receipts, {sum.NeedReview} need review, spent {Checker.Money(sum.Spent)}, deductible (est.) {Checker.Money(sum.Deductible)}");
            foreach (var l in sum.Lines) Console.WriteLine($"  {l.Code,-5} {l.Name,-44} {l.Count,3}  {Checker.Money(l.Spent),10}  ded {Checker.Money(l.Deductible),10}");
            if (o.Get("csv") is { } csvPath) { csvPath = Path.GetFullPath(csvPath); File.WriteAllText(csvPath, Export.Csv(store.All(), year, store.Settings)); Console.WriteLine($"\nCSV written to {csvPath}"); }
            return store.All().Any(r => r.Status == ReceiptStatus.Error) ? 3 : 0;
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
