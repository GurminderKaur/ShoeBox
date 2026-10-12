using System.Threading.Channels;

namespace Shoebox;

/// <summary>Reads queued receipts one at a time in the background, so the page stays responsive.</summary>
public sealed class Processor : BackgroundService
{
    private readonly Store _store;
    private readonly Reader _reader;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    public Processor(Store store, Reader reader)
    {
        _store = store;
        _reader = reader;
        foreach (var id in store.Pending()) _queue.Writer.TryWrite(id);
    }

    public void Enqueue(string id) => _queue.Writer.TryWrite(id);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stop))
        {
            var r = _store.Get(id);
            if (r == null || r.Status != ReceiptStatus.Processing) continue;
            await ProcessAsync(r, stop);
        }
    }

    public async Task ProcessAsync(Receipt r, CancellationToken ct = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(_store.FilesDir, r.StoredFile), ct);
            var (x, secs) = await _reader.ReadAsync(bytes, ct);
            Fill(r, x);
            r.Model = _reader.Model;
            r.Seconds = Math.Round(secs, 1);
            r.Error = null;
            Checker.Apply(r, _store.All(), DateTime.Today);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            r.Status = ReceiptStatus.Error;
            r.Error = ex is HttpRequestException
                ? "Couldn't reach Ollama. Make sure the Ollama app is running, then click Retry."
                : ex.Message;
        }
        _store.Update(r);
    }

    /// <summary>Copy the model's reading into the editable fields and choose a category.</summary>
    public static void Fill(Receipt r, Extraction x)
    {
        r.Raw = x;
        r.Vendor = x.Vendor;
        r.Date = x.Date;
        r.Subtotal = x.Subtotal;
        r.Gst = x.Gst;
        r.OtherTax = x.OtherTax;
        r.Tip = x.Tip;
        r.Total = x.Total;
        r.Items = x.Items;
        (r.Category, r.CategorySource) = Categories.Choose(x.Vendor, x.Items, x.Category);
        r.EditedByUser = false;
    }
}
