"""Score what the model read against the known values of the sample receipts.

Usage: python samples/score.py <shoebox data folder> [--markdown]
Prints field-by-field accuracy. Exit code is 0 even when the model misreads: the point is to measure, honestly.
"""
import json, os, re, sys

data_dir = sys.argv[1]
md = "--markdown" in sys.argv
here = os.path.dirname(os.path.abspath(__file__))
expected = {e["file"]: e for e in json.load(open(os.path.join(here, "expected.json")))}
receipts = json.load(open(os.path.join(data_dir, "receipts.json")))

norm = lambda s: re.sub(r"[^a-z0-9]", "", (s or "").lower())
fields = ["vendor", "date", "subtotal", "gst", "total", "category"]
hits = {f: 0 for f in fields}
rows = []
for r in receipts:
    e = expected.get(os.path.basename(r["fileName"]))
    if not e:
        continue
    raw = r.get("raw") or {}
    got = {
        "vendor": len(norm(raw.get("vendor"))) > 4 and (norm(e["vendor"]) in norm(raw.get("vendor")) or norm(raw.get("vendor")) in norm(e["vendor"])),
        "date": raw.get("date") == e["date"],
        "subtotal": abs(float(raw.get("subtotal", 0)) - e["subtotal"]) < 0.005,
        "gst": abs(float(raw.get("gst", 0)) - e["gst"]) < 0.005,
        "total": abs(float(raw.get("total", 0)) - e["total"]) < 0.005,
        "category": r.get("category") == e["cat"],  # final category (rule or AI)
    }
    for f in fields:
        hits[f] += got[f]
    rows.append((e["file"], r["status"], got, raw, r.get("checks", []) + r.get("cautions", []), r.get("seconds")))

n = len(rows)
if md:
    print(f"### Shoebox read {n} sample receipts with a local model\n")
    print("| Receipt | " + " | ".join(fields) + " | Status | Seconds |")
    print("|---|" + "---|" * len(fields) + "---|---|")
    for f, status, got, raw, checks, secs in rows:
        print(f"| {f} | " + " | ".join("✅" if got[k] else f"❌ `{raw.get(k, '')}`" for k in fields) + f" | {status} | {secs} |")
    print("\n**Accuracy:** " + ", ".join(f"{k} {hits[k]}/{n}" for k in fields))
    flagged = [f for f, status, got, *_ in rows if not all(got[k] for k in ["date", "subtotal", "gst", "total"])]
    caught = [f for f, status, got, *_ in rows if f in flagged and status != "ok"]
    print(f"\n**Misreads caught by the arithmetic checks:** {len(caught)}/{len(flagged)} receipts with a wrong amount or date were flagged \"needs a look\".")
else:
    for f, status, got, raw, checks, secs in rows:
        print(f"{f:20} {status:8} " + " ".join(("✓" if got[k] else "✗") + k for k in fields) + f"  ({secs}s)")
    print("accuracy:", {k: f"{hits[k]}/{n}" for k in fields})
