using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
#if ETIQ_BTW
using Etiq.Btw;
#endif
using Etiq.Core;

// etiq - Etiquette CLI (early scaffold).
// Working today:   inventory, previews, gallery, census
//                  (BTW recon, C# port of tools/recon/btwtool.py)
//                  validate (Etiquette SVG convention checks)
// Coming (roadmap): convert, print

if (args.Length < 2)
{
    Console.WriteLine("""
        etiq - Etiquette label tools

        usage:
          etiq validate  <file|dir> [configDir]
                check Etiquette SVG templates (convention.md); with a config
                dir containing printers.json, also run printer-feasibility
          etiq resolve   <file.svg> [--set Field=Value]... [--choose List=Key]...
                         [--csv records.csv] [--copies N]
                dry-run field resolution: shows the final string every field
                produces per label (serials come from a throwaway local
                counter — values are NOT reserved on the real service)
        """);
#if ETIQ_BTW
    Console.WriteLine("""
        BTW recon (etiquette-btw module present):
          etiq inventory <dir>            parse status + object classes for each .btw
          etiq previews  <dir> [out]      extract embedded label preview PNGs
          etiq gallery   <dir> [out.pdf]  contact-sheet PDF of every unique design
          etiq census    <dir> [out.csv]  serialization census
          etiq convert   <dir> [out]      .btw -> Etiquette SVG + fidelity.tsv (Phase 2 scaffold)
        """);
#endif
    return 1;
}

string cmd = args[0].ToLowerInvariant();
string dir = args[1];

if (cmd == "validate")
{
    var svgs = File.Exists(dir)
        ? new List<string> { dir }
        : Directory.EnumerateFiles(dir, "*.svg", new EnumerationOptions
          {
              RecurseSubdirectories = true,
              MatchCasing = MatchCasing.CaseInsensitive,
          }).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    Registry? registry = null;
    if (args.Length > 2 && Directory.Exists(args[2]))
    {
        registry = Registry.Load(args[2]);
        Console.WriteLine($"registry: {registry.Printers.Count} printer(s), {registry.Media.Count} media");
    }
    int errTotal = 0, warnTotal = 0;
    foreach (var fn in svgs)
    {
        List<Finding> findings;
        try
        {
            var tpl = EtiqTemplate.Load(fn);
            findings = TemplateValidator.Validate(tpl);
            if (registry is not null)
                foreach (var p in registry.Printers.Values)
                    findings.AddRange(FeasibilityChecker.Check(tpl, p));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            Console.WriteLine($"{fn}\n  ERROR  xml-parse          {ex.Message}");
            errTotal++;
            continue;
        }
        int e = findings.Count(f => f.Severity == Severity.Error);
        int w = findings.Count - e;
        errTotal += e; warnTotal += w;
        Console.WriteLine($"{fn}  {(e == 0 ? "OK" : "FAIL")}  ({e} errors, {w} warnings)");
        foreach (var f in findings)
            Console.WriteLine($"  {f}");
    }
    Console.WriteLine($"\n{svgs.Count} template(s): {errTotal} errors, {warnTotal} warnings");
    return errTotal == 0 ? 0 : 1;
}

if (cmd == "resolve")
{
    var prompts = new Dictionary<string, string>();
    var chosen = new Dictionary<string, string>();
    string? csvPath = null;
    int copies = 1;
    for (int i = 2; i < args.Length; i++)
        switch (args[i])
        {
            case "--set" when i + 1 < args.Length && args[i + 1].Contains('='):
                var kv = args[++i].Split('=', 2);
                prompts[kv[0]] = kv[1];
                break;
            case "--choose" when i + 1 < args.Length && args[i + 1].Contains('='):
                var ck = args[++i].Split('=', 2);
                chosen[ck[0]] = ck[1];
                break;
            case "--csv" when i + 1 < args.Length:
                csvPath = args[++i]; break;
            case "--copies" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
                copies = n; i++; break;
            default:
                Console.Error.WriteLine($"resolve: unrecognized argument '{args[i]}'");
                return 1;
        }

    var template = EtiqTemplate.Load(dir);
    var records = csvPath is null
        ? new List<Dictionary<string, string>> { new(StringComparer.OrdinalIgnoreCase) }
        : Etiq.Core.Csv.ReadFile(csvPath);

    string counterFile = Path.GetTempFileName();
    File.Delete(counterFile);
    try
    {
        var ctx = new ResolveContext
        {
            PromptValues = prompts,
            ListSelections = chosen,
            Counters = new LocalFileCounterProvider(counterFile),   // dry-run serials
            EpicorColumn = _ => null,   // no live ERP in a dry run; CSV columns still feed
        };
        var labels = BatchRunner.Run(template, records, ctx, copies);
        foreach (var l in labels)
        {
            Console.WriteLine($"--- label {l.LabelIndex}/{labels.Count} (record {l.RecordIndex}, copy {l.CopyIndex}) ---");
            foreach (var (name, value) in l.Fields.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {name,-18} = {value}");
        }
        Console.WriteLine($"\n{labels.Count} label(s) resolved (dry run: serials NOT reserved on the real counter service)");
        return 0;
    }
    catch (ResolveException ex)
    {
        Console.Error.WriteLine($"BLOCKED: {ex.Message}");
        return 1;
    }
    finally
    {
        if (File.Exists(counterFile)) File.Delete(counterFile);
    }
}
#if ETIQ_BTW
var files = Directory.EnumerateFiles(dir, "*.btw", new EnumerationOptions
{
    RecurseSubdirectories = true,
    MatchCasing = MatchCasing.CaseInsensitive,
}).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

switch (cmd)
{
    case "inventory":
    {
        int ok = 0, old = 0;
        var classHist = new SortedDictionary<string, int>();
        foreach (var fn in files)
        {
            var btw = BtwFile.ParseFile(fn);
            if (btw.IsOldFormat || btw.Document is null)
            {
                old++;
                Console.WriteLine($"OLD    {fn}");
                continue;
            }
            ok++;
            var cls = btw.ClassNames();
            foreach (var c in cls)
                classHist[c] = classHist.GetValueOrDefault(c) + 1;
            Console.WriteLine($"OK     {fn}  [{btw.Version} b{btw.Build}]  {btw.Document.Length}B  {string.Join(',', cls)}");
        }
        Console.WriteLine($"\nparsed: {ok}  old-format/unparsed: {old}");
        Console.WriteLine("\nclass histogram:");
        foreach (var (c, n) in classHist.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {n,5}x {c}");
        return 0;
    }
    case "previews":
    {
        string outDir = args.Length > 2 ? args[2] : "previews";
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var fn in files)
        {
            var btw = BtwFile.ParseFile(fn);
            if (btw.Previews.Count == 0) continue;
            File.WriteAllBytes(Path.Combine(outDir, SafeName(fn) + ".png"), btw.Previews[0]);
            n++;
        }
        Console.WriteLine($"{n} previews -> {outDir}");
        return 0;
    }
    case "gallery":
    {
        string outPdf = args.Length > 2 ? args[2] : "gallery.pdf";

        // Group files by identical preview bytes (unique designs).
        var byHash = new Dictionary<string, (byte[] Png, List<string> Files)>();
        foreach (var fn in files)
        {
            var btw = BtwFile.ParseFile(fn);
            if (btw.Previews.Count == 0) continue;
            string h = Convert.ToHexString(MD5.HashData(btw.Previews[0]));
            if (!byHash.TryGetValue(h, out var e))
                byHash[h] = e = (btw.Previews[0], new List<string>());
            e.Files.Add(fn);
        }
        var entries = byHash.Values
            .OrderBy(v => v.Files.Min(StringComparer.OrdinalIgnoreCase),
                     StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Layout matches btwtool.py: 4x5 cells, 200px thumbs, 26px caption, 8px pad.
        const int TH = 200, CAP = 26, COLS = 4, ROWS = 5, PAD = 8;
        const int CW = TH + PAD * 2, CH = TH + CAP + PAD * 2;
        var pdf = new PdfWriter(CW * COLS, CH * ROWS);
        int i = 0, pages = 0;
        foreach (var (png, fns) in entries)
        {
            if (i % (COLS * ROWS) == 0) { pdf.NewPage(); pages++; }
            int cell = i % (COLS * ROWS);
            double cx = cell % COLS * CW, cy = cell / COLS * CH;
            try
            {
                var thumb = PngImage.Decode(png).Thumbnail(TH);
                pdf.DrawImage(thumb,
                    cx + PAD + (TH - thumb.Width) / 2.0,
                    cy + PAD + (TH - thumb.Height) / 2.0,
                    thumb.Width, thumb.Height);
            }
            catch { /* undecodable preview: caption only */ }
            fns.Sort(StringComparer.OrdinalIgnoreCase);
            string name = Path.GetFileName(fns[0]);
            if (fns.Count > 1) name += $"  (+{fns.Count - 1})";
            pdf.DrawText(name.Length > 38 ? name[..38] : name, cx + PAD, cy + PAD + TH + 4, 11);
            i++;
        }
        if (pages == 0) { Console.WriteLine("no previews found"); return 1; }
        pdf.Save(outPdf);
        Console.WriteLine($"{entries.Count} unique designs, {pages} pages -> {outPdf}");
        return 0;
    }
    case "census":
    {
        string outCsv = args.Length > 2 ? args[2] : "census.csv";
        var serialDsRe = new Regex(@"^(Serial Numbers? \d|Bar Code Serial)");
        var labelSerialRe = new Regex(@"^serial[\s#:.]*(no|number|#|:)?[\s.()3-5S]*$",
                                      RegexOptions.IgnoreCase);
        var aiagRe = new Regex(@"\((3S|4S|5S|2S|S)\)");
        var numRe = new Regex(@"^\d{6,10}$");

        var rows = new List<Dictionary<string, string>>();
        foreach (var fn in files)
        {
            var btw = BtwFile.ParseFile(fn);
            if (btw.IsOldFormat || btw.Document is null)
            {
                rows.Add(new() { ["file"] = fn, ["status"] = "OLD-FORMAT" });
                continue;
            }
            var sset = btw.Utf16Strings().ToHashSet();
            string joined = string.Join('\n', sset);
            var serialDs = sset.Where(s => serialDsRe.IsMatch(s)).Order(StringComparer.Ordinal).ToList();
            var labelSerial = sset.Select(s => s.Trim())
                .Where(s => s.Length < 20 && labelSerialRe.IsMatch(s))
                .Distinct().Order(StringComparer.Ordinal).ToList();
            var aiag = aiagRe.Matches(joined).Select(m => m.Groups[1].Value)
                .Distinct().Order(StringComparer.Ordinal).ToList();
            var nums = sset.Where(s => numRe.IsMatch(s)).Order(StringComparer.Ordinal).ToList();
            rows.Add(new()
            {
                ["file"] = fn,
                ["status"] = "ok",
                ["serial_datasources"] = Trunc(string.Join("; ", serialDs), 80),
                ["serial_text_on_label"] = Trunc(string.Join("; ", labelSerial), 60),
                ["aiag_ids"] = string.Join(',', aiag),
                ["candidate_counter_values"] = Trunc(string.Join(',', nums.Take(8)), 100),
            });
        }

        string[] fields = { "file", "status", "serial_datasources",
                            "serial_text_on_label", "aiag_ids", "candidate_counter_values" };
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', fields));
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', fields.Select(f => Csv(r.GetValueOrDefault(f, "")))));
        File.WriteAllText(outCsv, sb.ToString());

        var okRows = rows.Where(r => r["status"] == "ok").ToList();
        Console.WriteLine($"{okRows.Count} parsed / {rows.Count} total -> {outCsv}");
        Console.WriteLine($"  with serialized data source: {okRows.Count(r => r["serial_datasources"] != "")}");
        Console.WriteLine($"  with serial shown on label:  {okRows.Count(r => r["serial_text_on_label"] != "" || r["aiag_ids"] != "")}");
        return 0;
    }
    case "convert":
    {
        string outDir = args.Length > 2 ? args[2] : "converted";
        Directory.CreateDirectory(outDir);
        var stats = new List<BtwConverter.ConvertStats>();
        int converted = 0, skipped = 0, invalid = 0;
        foreach (var fn in files)
        {
            var btw = BtwFile.ParseFile(fn);
            if (btw.IsOldFormat || btw.Document is null) { skipped++; continue; }

            // Preview ink-extent aspect ratio disambiguates the dims scan.
            double? aspect = null;
            if (btw.Previews.Count > 0)
                try { aspect = PreviewAspect(PngImage.Decode(btw.Previews[0])); }
                catch { /* undecodable preview: scan without aspect */ }

            var result = BtwConverter.Convert(btw, fn, aspect);
            string outFn = Path.Combine(outDir, SafeName(fn) + ".svg");
            File.WriteAllText(outFn, result.Svg);
            stats.Add(result.Stats);
            converted++;

            var errs = TemplateValidator.Validate(EtiqTemplate.Parse(result.Svg, outFn))
                .Where(f => f.Severity == Severity.Error).ToList();
            if (errs.Count > 0)
            {
                invalid++;
                Console.WriteLine($"INVALID {outFn}");
                foreach (var f in errs) Console.WriteLine($"  {f}");
            }
        }

        // fidelity report v1: structural coverage (SSIM waits on the renderer)
        var tsv = new StringBuilder("file\tdims\tW\tH\ttexts\twith_value\twith_width\tbarcodes\tbc_unmapped\tlines\toob\n");
        foreach (var st in stats)
            tsv.AppendLine($"{st.File}\t{(st.DimsFound ? "ok" : "GUESS")}\t{st.LabelW}\t{st.LabelH}" +
                           $"\t{st.Texts}\t{st.TextsWithValue}\t{st.TextsWithWidth}" +
                           $"\t{st.Barcodes}\t{st.BarcodesUnmapped}\t{st.Lines}\t{st.AnchorsOutOfBounds}");
        File.WriteAllText(Path.Combine(outDir, "fidelity.tsv"), tsv.ToString());

        Console.WriteLine($"\n{converted} converted, {skipped} skipped (old/unparsed), " +
                          $"{invalid} with validation errors -> {outDir}");
        Console.WriteLine($"dims found: {stats.Count(s => s.DimsFound)}/{stats.Count}; " +
                          $"texts {stats.Sum(s => s.Texts)} (value {stats.Sum(s => s.TextsWithValue)}, width {stats.Sum(s => s.TextsWithWidth)}); " +
                          $"barcodes {stats.Sum(s => s.Barcodes)}; lines {stats.Sum(s => s.Lines)}");
        return 0;
    }
    default:
        Console.Error.WriteLine($"unknown command: {cmd}");
        return 1;
}

static double? PreviewAspect(PngImage png)
{
    // Bounding box of pixels that differ from the corner color (background).
    int w = png.Width, h = png.Rgb.Length / (png.Width * 3);
    byte cr = png.Rgb[0], cg = png.Rgb[1], cb = png.Rgb[2];
    int minX = w, maxX = -1, minY = h, maxY = -1;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = (y * w + x) * 3;
            if (png.Rgb[p] != cr || png.Rgb[p + 1] != cg || png.Rgb[p + 2] != cb)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
    if (maxX < 0 || maxY <= minY) return null;
    return (maxX - minX + 1) / (double)(maxY - minY + 1);
}
#else
Console.Error.WriteLine($"unknown command: {cmd} (BTW recon commands require the etiquette-btw module)");
return 1;
#endif

static string SafeName(string fn) => string.Concat(
    Path.GetFileNameWithoutExtension(fn)
        .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

static string Csv(string s) =>
    s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
        ? '"' + s.Replace("\"", "\"\"") + '"' : s;
