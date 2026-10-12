namespace Shoebox;

public enum ReceiptStatus { Processing, Ok, Review, Error }

/// <summary>What the model read off the receipt image (raw, before checks).</summary>
public sealed class Extraction
{
    public bool IsReceipt { get; set; } = true;
    public string Vendor { get; set; } = "";
    public string Date { get; set; } = "";          // yyyy-MM-dd, or "" if unreadable
    public decimal Subtotal { get; set; }            // before tax; 0 = not shown
    public decimal Gst { get; set; }                 // GST (or GST part of HST); 0 = none shown
    public decimal OtherTax { get; set; }            // PST / provincial part, out-of-province only
    public decimal Tip { get; set; }
    public decimal Total { get; set; }
    public string Items { get; set; } = "";          // short description of what was bought
    public string Category { get; set; } = "";       // suggested T2125 line code
}

public sealed class Receipt
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";       // original name
    public string StoredFile { get; set; } = "";     // name inside the data folder
    public DateTime AddedUtc { get; set; }
    public ReceiptStatus Status { get; set; } = ReceiptStatus.Processing;
    public string? Error { get; set; }

    // Editable fields (start as the model's reading; the user can correct them)
    public string Vendor { get; set; } = "";
    public string Date { get; set; } = "";
    public decimal Subtotal { get; set; }
    public decimal Gst { get; set; }
    public decimal OtherTax { get; set; }
    public decimal Tip { get; set; }
    public decimal Total { get; set; }
    public string Items { get; set; } = "";
    public string Category { get; set; } = Categories.Other;
    public string CategorySource { get; set; } = "";  // "rule", "ai" or "you"
    public string Notes { get; set; } = "";
    public bool EditedByUser { get; set; }

    public List<string> Checks { get; set; } = new();    // problems: amounts don't add up, no total/date, etc.
    public List<string> Cautions { get; set; } = new();  // worth knowing (PST, unusual GST rate, possible duplicate); cleared from "needs a look" once you confirm
    public Extraction? Raw { get; set; }                 // exactly what the model returned
    public string? Model { get; set; }
    public double? Seconds { get; set; }
}

public sealed class Settings
{
    /// <summary>Registered for GST? Registrants claim GST back as input tax credits (ITCs).</summary>
    public bool GstRegistered { get; set; }
    /// <summary>Share of vehicle use that is for business, 0–100.</summary>
    public int VehicleBusinessPercent { get; set; } = 100;
    /// <summary>Share of phone/internet use that is for business, 0–100.</summary>
    public int PhoneBusinessPercent { get; set; } = 100;
}
