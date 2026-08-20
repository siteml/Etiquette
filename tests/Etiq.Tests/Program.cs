using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
#if ETIQ_BTW
using Etiq.Btw;
#endif
using Etiq.Core;

// Etiq.Tests - dependency-free test runner (no xunit; locked-down machines).
// Run: dotnet run --project tests/Etiq.Tests [-- <repoRoot>]
// Exit code = number of failures.

// --dump-barcodes <dir>: render a deterministic sample set of QR /
// DataMatrix / PDF417 PNGs + manifest.tsv for external decode
// verification (zbar / zxing) — a debug tool, not part of the suite.
if (args.Length >= 2 && args[0] == "--dump-barcodes")
{
    BarcodeDump.Run(args[1]);
    return 0;
}

string repoRoot = args.Length > 0 ? args[0] : FindRepoRoot();
int passed = 0, failed = 0;

void Check(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {ex.Message.ReplaceLineEndings("\n      ")}");
    }
}
void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }
void AssertEq<T>(T expect, T actual, string what) =>
    Assert(Equals(expect, actual), $"{what}: expected {expect}, got {actual}");

// ---------- template validation ----------

Check("examples validate clean", () =>
{
    foreach (var fn in Directory.GetFiles(Path.Combine(repoRoot, "examples"), "*.svg"))
    {
        var findings = TemplateValidator.Validate(EtiqTemplate.Load(fn));
        var errs = findings.Where(f => f.Severity == Severity.Error).ToList();
        Assert(errs.Count == 0, $"{Path.GetFileName(fn)}: {string.Join("; ", errs)}");
    }
});

Check("undeclared field detected", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <text x="8" y="20" data-field="Ghost">x</text>
        </svg>
        """);
    Assert(TemplateValidator.Validate(t).Any(f => f.Code == "field-undeclared"),
           "expected field-undeclared");
});

Check("serial without counter, bad symbology, degenerate rect, out-of-bounds", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="S" source="serial"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="S">000001</text>
          <rect x="8" y="30" width="0" height="40" data-barcode="code999" data-field="S"/>
          <text x="500" y="20">off the label</text>
        </svg>
        """);
    var codes = TemplateValidator.Validate(t).Select(f => f.Code).ToHashSet();
    foreach (var c in new[] { "serial-counter", "barcode-symbology", "barcode-degenerate", "out-of-bounds" })
        Assert(codes.Contains(c), $"expected {c}, got: {string.Join(",", codes)}");
});

Check("module-mils below AIAG warns", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <rect x="8" y="8" width="100" height="40" data-barcode="code128"
                data-value="X" data-module-mils="7"/>
        </svg>
        """);
    Assert(TemplateValidator.Validate(t).Any(
        f => f.Code == "barcode-module" && f.Severity == Severity.Warning), "expected warn");
});

// ---------- convention 0.2: compose / maps / rest / reserved / layers / text fit ----------

Check("0.2 valid kitchen-sink template has no errors", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="PartNo" source="prompt" caption="Part:"/>
            <etiq:field name="Plant" source="rest" connection="glpi"
                        query="assets" pick="assets[0].location" on-fail="cached"/>
            <etiq:field name="Serial" source="serial" counter="DUDEK"/>
            <etiq:field name="Tag" source="compose">
              <etiq:seg value="DB-"/>
              <etiq:seg ref="PartNo" start="0" len="4" case="upper"/>
              <etiq:seg ref="Plant" map="Plants" default="XX"/>
              <etiq:seg ref="Serial" pad="left:0:6"/>
            </etiq:field>
            <etiq:map name="Plants" default="??">
              <etiq:when from="CHICAGO" to="CH"/>
              <etiq:when prefix="SALT" to="SA"/>
            </etiq:map>
          </etiq:label></metadata>
          <g data-layer="Frame" data-locked="true">
            <rect x="2" y="2" width="284" height="92" fill="none" stroke="black"/>
          </g>
          <g data-layer="Fields">
            <text x="8" y="20" data-field="Tag" data-width="200" data-overflow="shrink">DB-SAMP-CH-000001</text>
            <rect x="200" y="8" width="80" height="80" data-barcode="qr" data-field="Tag"/>
          </g>
          <g data-layer="Guides" data-print="false" display="none">
            <text x="8" y="90">margin note</text>
          </g>
        </svg>
        """);
    var errs = TemplateValidator.Validate(t).Where(f => f.Severity == Severity.Error).ToList();
    Assert(errs.Count == 0, string.Join("; ", errs));
});

Check("compose: bad segs, bad ref, nested compose, bad pad", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="A" source="fixed" value="x"/>
            <etiq:field name="C1" source="compose">
              <etiq:seg value="lit" ref="A"/>
              <etiq:seg ref="Ghost"/>
              <etiq:seg ref="C2" pad="mid:0:6"/>
            </etiq:field>
            <etiq:field name="C2" source="compose"/>
            <etiq:field name="NotCompose" source="fixed" value="y">
              <etiq:seg value="z"/>
            </etiq:field>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="C1">preview</text>
          <text x="8" y="40" data-field="NotCompose">y</text>
        </svg>
        """);
    var codes = TemplateValidator.Validate(t).Select(f => f.Code).ToHashSet();
    foreach (var c in new[] { "seg-content", "seg-ref", "seg-pad", "compose-empty", "compose-segs" })
        Assert(codes.Contains(c), $"expected {c}, got: {string.Join(",", codes)}");
});

Check("maps: unresolved ref, no default warns, bad when", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="A" source="fixed" value="x"/>
            <etiq:field name="C" source="compose">
              <etiq:seg ref="A" map="NoSuchMap"/>
              <etiq:seg ref="A" map="NoDefault"/>
            </etiq:field>
            <etiq:map name="NoDefault">
              <etiq:when from="X" prefix="Y" to="Z"/>
              <etiq:when from="Q"/>
            </etiq:map>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="C">preview</text>
        </svg>
        """);
    var findings = TemplateValidator.Validate(t);
    Assert(findings.Any(f => f.Code == "seg-map"), "expected seg-map error");
    Assert(findings.Any(f => f.Code == "map-default" && f.Severity == Severity.Warning), "expected map-default warn");
    Assert(findings.Count(f => f.Code == "map-when") >= 2, "expected map-when errors (both-set and missing to=)");
});

Check("rest: missing connection/pick, bad pick, bad on-fail", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="R1" source="rest"/>
            <etiq:field name="R2" source="rest" connection="glpi" pick="$.assets[*].name"/>
            <etiq:field name="R3" source="rest" connection="glpi" pick="a.b" on-fail="retry"/>
            <etiq:field name="F" source="fixed" value="x" on-fail="block"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="R1">a</text>
          <text x="8" y="40" data-field="R2">b</text>
          <text x="8" y="60" data-field="R3">c</text>
          <text x="8" y="80" data-field="F">d</text>
        </svg>
        """);
    var findings = TemplateValidator.Validate(t);
    var codes = findings.Select(f => f.Code).ToHashSet();
    foreach (var c in new[] { "field-rest", "field-pick", "on-fail" })
        Assert(codes.Contains(c), $"expected {c}, got: {string.Join(",", codes)}");
    Assert(findings.Any(f => f.Code == "on-fail" && f.Severity == Severity.Warning),
           "expected on-fail-on-fixed warning");
});

Check("reserved kinds: structural checks + reserved warning", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="D" source="db" connection="mes"/>
            <etiq:field name="Fi" source="file" path="\\srv\lots.csv" column="Lot" match-column="Part"/>
            <etiq:field name="W" source="device" connection="scale1"/>
            <etiq:field name="OK" source="file" path="a.csv" column="C"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="D">a</text>
          <text x="8" y="40" data-field="Fi">b</text>
          <text x="8" y="60" data-field="W">c</text>
          <text x="8" y="80" data-field="OK">d</text>
        </svg>
        """);
    var findings = TemplateValidator.Validate(t);
    var codes = findings.Select(f => f.Code).ToHashSet();
    Assert(codes.Contains("field-db"), "db missing query/column");
    Assert(codes.Contains("field-file"), "file match-column without match-value");
    Assert(findings.Count(f => f.Code == "field-reserved" && f.Severity == Severity.Warning) == 4,
           "each reserved field warns once");
    Assert(!findings.Any(f => f.Code == "field-source"), "reserved kinds are not unknown");
    Assert(!findings.Any(f => f.Code == "field-device" ), "device with connection is structurally fine");
});

Check("layers: dup name, misplaced attr, no-print with fields, bad data-print", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="A" source="fixed" value="x"/>
          </etiq:label></metadata>
          <g data-layer="L1"><text x="8" y="20">a</text></g>
          <g data-layer="L1"><text x="8" y="40">b</text></g>
          <g data-layer="L2" data-print="maybe">
            <g data-layer="Nested"><text x="8" y="60">c</text></g>
          </g>
          <g data-layer="L3" data-print="false">
            <text x="8" y="80" data-field="A">x</text>
          </g>
        </svg>
        """);
    var findings = TemplateValidator.Validate(t);
    var codes = findings.Select(f => f.Code).ToHashSet();
    foreach (var c in new[] { "layer-dup", "layer-misplaced", "layer-print" })
        Assert(codes.Contains(c), $"expected {c}, got: {string.Join(",", codes)}");
    Assert(findings.Any(f => f.Code == "layer-print" && f.Severity == Severity.Warning),
           "expected no-print-with-fields warning");
});

Check("text fit: overflow without width, wrap without height, box out of bounds", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <text x="8" y="20" data-overflow="shrink">a</text>
          <text x="8" y="40" data-width="50" data-overflow="wrap">b</text>
          <text x="200" y="60" data-width="150">c</text>
          <text x="144" y="80" text-anchor="middle" data-width="200" data-overflow="clip">ok centered</text>
        </svg>
        """);
    var fit = TemplateValidator.Validate(t).Where(f => f.Code == "text-fit").ToList();
    Assert(fit.Any(f => f.Message.Contains("requires data-width")), "overflow w/o width");
    Assert(fit.Any(f => f.Message.Contains("requires a positive data-height")), "wrap w/o height");
    Assert(fit.Any(f => f.Message.Contains("exceeds viewBox width")), "box out of bounds");
    AssertEq(3, fit.Count, "centered 200-wide box at x=144 is legal, no extra findings");
});

Check("compose seg ref marks target field as used", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Inner" source="fixed" value="x"/>
            <etiq:field name="C" source="compose"><etiq:seg ref="Inner"/></etiq:field>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="C">x</text>
        </svg>
        """);
    Assert(!TemplateValidator.Validate(t).Any(f => f.Code == "field-unused"),
           "Inner is used via seg ref");
});

Check("on-fail use: feeding a barcode warns (direct + via compose)", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="R" source="rest" connection="glpi" pick="a.b" on-fail="use:NA"/>
            <etiq:field name="C" source="compose"><etiq:seg ref="R"/></etiq:field>
          </etiq:label></metadata>
          <rect x="8" y="8" width="80" height="80" data-barcode="qr" data-field="R"/>
          <rect x="100" y="8" width="80" height="80" data-barcode="qr" data-field="C"/>
        </svg>
        """);
    AssertEq(2, TemplateValidator.Validate(t).Count(f => f.Code == "barcode-onfail"),
             "direct + compose-ref warnings");
});

// ---------- field resolver (convention 0.2 runtime) ----------

Check("resolver: kitchen-sink compose resolves end-to-end", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="PartNo" source="prompt" caption="Part:"/>
            <etiq:field name="Plant" source="rest" connection="glpi" pick="assets[0].location"/>
            <etiq:field name="Serial" source="serial" counter="DUDEK" format="000000"/>
            <etiq:field name="Date" source="auto" value="date:yyMM"/>
            <etiq:field name="Tag" source="compose">
              <etiq:seg value="DB-"/>
              <etiq:seg ref="PartNo" start="0" len="4" case="upper"/>
              <etiq:seg value="-"/>
              <etiq:seg ref="Plant" map="Plants"/>
              <etiq:seg value="-"/>
              <etiq:seg ref="Date"/>
              <etiq:seg value="-"/>
              <etiq:seg ref="Serial"/>
            </etiq:field>
            <etiq:map name="Plants" default="XX">
              <etiq:when from="CHICAGO" to="CH"/>
              <etiq:when prefix="SALT" to="SA"/>
            </etiq:map>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="Tag">preview</text>
        </svg>
        """);
    string tmp = Path.GetTempFileName(); File.Delete(tmp);
    var ctx = new ResolveContext
    {
        PromptValues = { ["PartNo"] = "widget-9000" },
        Rest = (conn, q, pick) => conn == "glpi" ? "SALTILLO" : null,
        Counters = new LocalFileCounterProvider(tmp),
        Now = new DateTime(2026, 8, 2),
    };
    var r = new FieldResolver(t, ctx);
    AssertEq("DB-WIDG-SA-2608-000001", r.Resolve("Tag"), "composed tag");
    File.Delete(tmp);
});

Check("resolver: serial memoized once per label, fresh per label", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="S" source="serial" counter="A"/>
            <etiq:field name="C1" source="compose"><etiq:seg ref="S"/><etiq:seg ref="S"/></etiq:field>
            <etiq:field name="C2" source="compose"><etiq:seg ref="S"/></etiq:field>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="C1">p</text>
        </svg>
        """);
    string tmp = Path.GetTempFileName(); File.Delete(tmp);
    var counters = new LocalFileCounterProvider(tmp);
    var r1 = new FieldResolver(t, new ResolveContext { Counters = counters });
    var all = r1.ResolveAll();
    AssertEq("11", all["C1"], "same reservation twice within label");
    AssertEq("1", all["C2"], "same reservation across fields");
    var r2 = new FieldResolver(t, new ResolveContext { Counters = counters });
    AssertEq("2", r2.Resolve("S"), "next label gets next value");
    File.Delete(tmp);
});

Check("resolver: on-fail block/cached/use + cache write-through", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="B" source="rest" connection="c" pick="a" on-fail="block"/>
            <etiq:field name="C" source="rest" connection="c" pick="a" on-fail="cached"/>
            <etiq:field name="U" source="rest" connection="c" pick="a" on-fail="use:N/A"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="B">p</text>
        </svg>
        """);
    var cache = new Dictionary<string, string> { ["C"] = "old-value" };
    string? logged = null;
    var failing = new ResolveContext
    {
        Rest = (_, _, _) => throw new IOException("timeout"),
        Cache = cache,
        OnCachedValueUsed = f => logged = f,
    };
    var r = new FieldResolver(t, failing);
    try { r.Resolve("B"); throw new Exception("block did not throw"); }
    catch (ResolveException ex) { Assert(ex.Field == "B", "names the field"); }
    AssertEq("old-value", r.Resolve("C"), "cached fallback");
    AssertEq("C", logged!, "cached use logged");
    AssertEq("N/A", r.Resolve("U"), "use: literal");

    var working = new ResolveContext { Rest = (_, _, _) => "fresh", Cache = cache };
    AssertEq("fresh", new FieldResolver(t, working).Resolve("C"), "fetch ok");
    AssertEq("fresh", cache["C"], "cache write-through");
});

Check("resolver: maps exact>prefix>default precedence, no-default blocks", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="In" source="prompt" caption="v:"/>
            <etiq:field name="M" source="compose"><etiq:seg ref="In" map="T"/></etiq:field>
            <etiq:field name="MD" source="compose"><etiq:seg ref="In" map="T" default="segdflt"/></etiq:field>
            <etiq:field name="N" source="compose"><etiq:seg ref="In" map="NoD"/></etiq:field>
            <etiq:map name="T" default="mapdflt">
              <etiq:when prefix="AB" to="prefix-hit"/>
              <etiq:when from="ABC" to="exact-hit"/>
            </etiq:map>
            <etiq:map name="NoD"><etiq:when from="X" to="Y"/></etiq:map>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="M">p</text>
        </svg>
        """);
    string Res(string field, string input) =>
        new FieldResolver(t, new ResolveContext { PromptValues = { ["In"] = input } })
            .Resolve(field);
    AssertEq("exact-hit", Res("M", "ABC"), "exact beats prefix even in doc order");
    AssertEq("prefix-hit", Res("M", "ABZ"), "prefix fallback");
    AssertEq("segdflt", Res("MD", "zzz"), "seg default beats map default");
    AssertEq("mapdflt", Res("M", "zzz"), "map default");
    try { Res("N", "zzz"); throw new Exception("no-default did not block"); }
    catch (ResolveException) { }
});

Check("resolver: if-empty, required, reserved kinds fail at print", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="E" source="prompt" caption="v:" if-empty="fallback"/>
            <etiq:field name="R" source="prompt" caption="v:" required="true"/>
            <etiq:field name="W" source="device" connection="scale1"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="E">p</text>
        </svg>
        """);
    var r = new FieldResolver(t, new ResolveContext());
    AssertEq("fallback", r.Resolve("E"), "if-empty");
    try { r.Resolve("R"); throw new Exception("required-empty did not block"); }
    catch (ResolveException) { }
    try { r.Resolve("W"); throw new Exception("reserved kind did not fail"); }
    catch (ResolveException ex) { Assert(ex.Message.Contains("reserved"), "reserved message"); }
});

Check("resolver: field case= is opt-in, normalizes, validator gates enum", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Lot" source="prompt" caption="Lot:" case="upper"/>
            <etiq:field name="Note" source="prompt" caption="Note:"/>
            <etiq:field name="Co" source="prompt" caption="Co:" case="title"/>
            <etiq:field name="Nm" source="prompt" caption="Nm:" case="normal"/>
            <etiq:field name="Bad" source="prompt" caption="x:" case="proper"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="Lot">l</text>
          <text x="8" y="40" data-field="Note">n</text>
          <text x="8" y="55" data-field="Co">c</text>
          <text x="8" y="70" data-field="Nm">m</text>
          <text x="8" y="85" data-field="Bad">b</text>
        </svg>
        """);
    var r = new FieldResolver(t, new ResolveContext
        { PromptValues = { ["Lot"] = "ab-12x", ["Note"] = "Mixed Case",
                           ["Co"] = "DUDEK and BOCK mfg", ["Nm"] = "AsIs Text", ["Bad"] = "q" } });
    AssertEq("AB-12X", r.Resolve("Lot"), "declared upper normalizes");
    AssertEq("Mixed Case", r.Resolve("Note"), "no case= -> untouched (never assumed)");
    AssertEq("Dudek and Bock Mfg", r.Resolve("Co"), "English title: small word lowered, last word capped");
    AssertEq("AsIs Text", r.Resolve("Nm"), "normal = explicit no-op");
    AssertEq("The Fox and the Dog", FieldResolver.EnglishTitleCase("THE FOX AND THE DOG"), "articles lowered mid-title");
    AssertEq("A Tale: Of Two Labels", FieldResolver.EnglishTitleCase("a tale: of two labels"), "cap after colon");
    AssertEq("First-Class Parts to Go", FieldResolver.EnglishTitleCase("FIRST-CLASS PARTS TO GO"), "hyphen compound + small words");
    AssertEq("State of the Art", FieldResolver.EnglishTitleCase("state of the art"), "classic");
    Assert(TemplateValidator.Validate(t).Any(
        f => f.Code == "field-case" && f.Severity == Severity.Error), "bad enum rejected");
});

Check("resolver: transform order + number/date formats", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="In" source="fixed" value="ab7"/>
            <etiq:field name="T" source="compose">
              <etiq:seg ref="In" start="2" len="1" format="number:0000" pad="left:*:6"/>
            </etiq:field>
            <etiq:field name="D" source="compose">
              <etiq:seg value="2026-08-02" format="date:dd-MMM-yyyy" case="upper"/>
            </etiq:field>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="T">p</text>
        </svg>
        """);
    var r = new FieldResolver(t, new ResolveContext());
    AssertEq("**0007", r.Resolve("T"), "substr->number->pad order");
    AssertEq("02-AUG-2026", r.Resolve("D"), "date format then upper");
});

// ---------- embedded pick lists ----------

Check("pick lists: set behavior, default, no-selection blocks, validator", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:list name="ShipTo" key="Name" default="Chicago Plant">
              <etiq:row Name="Chicago Plant" Addr="5100 W. Roosevelt Rd" City="Chicago, IL"/>
              <etiq:row Name="Saltillo Plant" Addr="Blvd. Industria 100" City="Saltillo, Coah."/>
            </etiq:list>
            <etiq:list name="Dept" key="Code">
              <etiq:row Code="RCV"/>
              <etiq:row Code="SHP"/>
            </etiq:list>
            <etiq:field name="ShipName" source="list" list="ShipTo" column="Name"/>
            <etiq:field name="ShipAddr" source="list" list="ShipTo" column="Addr"/>
            <etiq:field name="ShipCity" source="list" list="ShipTo" column="City"/>
            <etiq:field name="Dept" source="list" list="Dept" column="Code"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="ShipName">n</text>
          <text x="8" y="40" data-field="ShipAddr">a</text>
          <text x="8" y="60" data-field="ShipCity">c</text>
          <text x="8" y="80" data-field="Dept">d</text>
        </svg>
        """);
    Assert(TemplateValidator.Validate(t).All(f => f.Severity != Severity.Error),
           "clean template validates");

    // one selection drives the whole address block (set behavior)
    var r = new FieldResolver(t, new ResolveContext
        { ListSelections = { ["ShipTo"] = "Saltillo Plant", ["Dept"] = "RCV" } });
    AssertEq("Saltillo Plant", r.Resolve("ShipName"), "name follows selection");
    AssertEq("Blvd. Industria 100", r.Resolve("ShipAddr"), "addr follows same row");
    AssertEq("RCV", r.Resolve("Dept"), "independent list picks independently");

    // default row when nothing selected; no default + no selection blocks
    var r2 = new FieldResolver(t, new ResolveContext());
    AssertEq("5100 W. Roosevelt Rd", r2.Resolve("ShipAddr"), "default row");
    try { r2.Resolve("Dept"); throw new Exception("no-default did not block"); }
    catch (ResolveException ex) { Assert(ex.Message.Contains("no row selected"), ex.Message); }

    // bad selection names the row
    try
    {
        new FieldResolver(t, new ResolveContext { ListSelections = { ["ShipTo"] = "Mars" } })
            .Resolve("ShipName");
        throw new Exception("unknown key accepted");
    }
    catch (ResolveException) { }
});

Check("pick lists: validator catches structural problems", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:list name="L" key="K" default="nope">
              <etiq:row K="A" Extra="x"/>
              <etiq:row K="A"/>
              <etiq:row Other="y"/>
            </etiq:list>
            <etiq:field name="F1" source="list" list="Ghost" column="K"/>
            <etiq:field name="F2" source="list" list="L" column="NoSuchCol"/>
            <etiq:field name="F3" source="list" list="L" column="Extra"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="F1">a</text>
          <text x="8" y="40" data-field="F2">b</text>
          <text x="8" y="60" data-field="F3">c</text>
        </svg>
        """);
    var findings = TemplateValidator.Validate(t);
    var codes = findings.Select(f => f.Code).ToList();
    Assert(codes.Count(c => c == "list-key") >= 2, "dup key + missing key");
    Assert(codes.Contains("list-default"), "default matches no row");
    Assert(findings.Count(f => f.Code == "field-list" && f.Severity == Severity.Error) >= 2,
           "undeclared list + absent column are errors");
    Assert(findings.Any(f => f.Code == "field-list" && f.Severity == Severity.Warning),
           "sparse column is a warning");
});

// ---------- csv + batch merge ----------

Check("Csv: quotes, embedded commas/newlines, CRLF, short rows", () =>
{
    var rows = Csv.Read("Part,Qty,Note\r\nP-1,100,\"a, \"\"quoted\"\"\nnote\"\r\nP-2,50\r\n");
    AssertEq(2, rows.Count, "rows");
    AssertEq("P-1", rows[0]["part"], "case-insensitive column");
    AssertEq("a, \"quoted\"\nnote", rows[0]["Note"], "quoted field");
    AssertEq("", rows[1]["Note"], "short row padded");
});

Check("BatchRunner: records x copies, autos, per-label serials, CSV feeds epicor", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Part" source="epicor" column="PartNum"/>
            <etiq:field name="Op" source="prompt" caption="Operator:"/>
            <etiq:field name="Ser" source="serial" counter="B" format="000"/>
            <etiq:field name="NofM" source="compose">
              <etiq:seg ref="Idx"/><etiq:seg value=" of "/><etiq:seg ref="Cnt"/>
            </etiq:field>
            <etiq:field name="Idx" source="auto" value="labelindex"/>
            <etiq:field name="Cnt" source="auto" value="labelcount"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="Part">p</text>
          <text x="8" y="40" data-field="NofM">1 of 1</text>
          <text x="8" y="60" data-field="Ser">000</text>
          <text x="8" y="80" data-field="Op">op</text>
        </svg>
        """);
    string tmp = Path.GetTempFileName(); File.Delete(tmp);
    var records = Csv.Read("PartNum\nW-100\nW-200\n");
    var labels = BatchRunner.Run(t, records, new ResolveContext
    {
        PromptValues = { ["Op"] = "SITE" },
        Counters = new LocalFileCounterProvider(tmp),
    }, copiesPerRecord: 2);

    AssertEq(4, labels.Count, "2 records x 2 copies");
    AssertEq("W-100", labels[0].Fields["Part"], "record 1 column");
    AssertEq("W-200", labels[2].Fields["Part"], "record 2 column");
    AssertEq("1 of 4", labels[0].Fields["NofM"], "labelindex/count");
    AssertEq("4 of 4", labels[3].Fields["NofM"], "last label");
    AssertEq(2, labels[1].CopyIndex, "copy index");
    Assert(labels.Select(l => l.Fields["Ser"]).Distinct().Count() == 4,
           "every physical label gets its own serial");
    AssertEq("004", labels[3].Fields["Ser"], "serials sequential");
    Assert(labels.All(l => l.Fields["Op"] == "SITE"), "prompt asked once, shared");
    File.Delete(tmp);
});

Check("BatchRunner: blocking failure names label/record", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Part" source="epicor" column="PartNum" required="true"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="Part">p</text>
        </svg>
        """);
    var records = Csv.Read("PartNum,Qty\nW-100,1\n,1\n");   // second record: empty PartNum
    try
    {
        BatchRunner.Run(t, records, new ResolveContext());
        throw new Exception("empty required did not block");
    }
    catch (ResolveException ex)
    {
        Assert(ex.Message.Contains("record 2"), "names the record: " + ex.Message);
    }
});

// ---------- editor core: doc model, undo, hit-testing ----------

Check("editor: foreign content round-trips untouched", () =>
{
    string src = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- designer comment -->
        <svg xmlns="http://www.w3.org/2000/svg"
             xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
             xmlns:etiq="https://etiquette.dev/ns/0.1"
             width="3in" height="1in" viewBox="0 0 288 96"
             inkscape:version="1.3">
          <metadata><etiq:label><etiq:field name="A" source="fixed" value="x"/></etiq:label></metadata>
          <g data-layer="L1" inkscape:groupmode="layer" inkscape:label="L1">
            <text x="10" y="20" font-size="12" data-field="A"
                  style="mystery:kept" data-custom="alien">hello</text>
          </g>
        </svg>
        """;
    var doc = Etiq.Editor.Core.EditorDoc.Parse(src);
    var obj = doc.Objects.Single();
    doc.Undo.Push(obj.Move(5, 5));
    doc.Undo.Undo();
    string outp = doc.Xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    foreach (var keep in new[] { "designer comment", "inkscape:version=\"1.3\"",
             "inkscape:groupmode=\"layer\"", "style=\"mystery:kept\"",
             "data-custom=\"alien\"", "etiq:field" })
        Assert(outp.Contains(keep), $"lost: {keep}");
    Assert(outp.Contains("x=\"10\""), "move undone");
});

Check("editor: drag merges into one undo step; undo/redo restore exactly", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <g data-layer="L"><rect x="10" y="10" width="40" height="20"/></g>
        </svg>
        """);
    var o = doc.Objects.Single();
    for (int i = 0; i < 5; i++) doc.Undo.Push(o.Move(2, 1));   // one drag, 5 steps
    AssertEq(1, doc.Undo.Count, "merged to one entry");
    AssertEq(20.0, o.GetNum("x"), "x after drag");
    doc.Undo.Undo();
    AssertEq(10.0, o.GetNum("x"), "undo restores pre-drag");
    AssertEq(10.0, o.GetNum("y"), "y too");
    doc.Undo.Redo();
    AssertEq(20.0, o.GetNum("x"), "redo replays whole drag");
    AssertEq(15.0, o.GetNum("y"), "y replayed");
});

Check("editor: rotated hit-test + handles + resize math", () =>
{
    // 100x20 rect at (0,40), rotated 90° about its center (50,50)
    var r = new Etiq.Editor.Core.RectD(0, 40, 100, 20);
    var pivot = r.Center;
    Assert(Etiq.Editor.Core.Geometry.HitRotatedRect(
        new(50, 5), r, 90, pivot), "point on rotated extent hits");
    Assert(!Etiq.Editor.Core.Geometry.HitRotatedRect(
        new(90, 50), r, 90, pivot), "point on unrotated extent misses");

    var h = Etiq.Editor.Core.Geometry.HitHandle(
        new(60, 0), r, 90, pivot, radius: 3);
    Assert(h is not null, "handle under rotated position found");

    var resized = Etiq.Editor.Core.Geometry.ResizeBy(
        new(10, 10, 40, 20), Etiq.Editor.Core.Handle.SE, new(70, 50));
    AssertEq(60.0, resized.W, "SE drag width");
    AssertEq(40.0, resized.H, "SE drag height");
    var minned = Etiq.Editor.Core.Geometry.ResizeBy(
        new(10, 10, 40, 20), Etiq.Editor.Core.Handle.E, new(5, 10), min: 2);
    AssertEq(2.0, minned.W, "min size enforced");

    AssertEq(15.0, Etiq.Editor.Core.Geometry.Snap(14.2, 5), "snap");
});

Check("editor: layers, z-order, delete/undo, promote group", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <g data-layer="L">
            <rect id="a" x="0" y="0" width="10" height="10"/>
            <rect id="b" x="20" y="0" width="10" height="10"/>
            <g id="grp"><line x1="0" y1="50" x2="50" y2="50" stroke-width="2"/></g>
          </g>
        </svg>
        """);
    var layer = doc.Layers.Single();
    var a = doc.Objects.First(o => (string?)o.El.Attribute("id") == "a");

    doc.ReorderZ(a, forward: true);
    AssertEq("b", a.El.PreviousNode is System.Xml.Linq.XElement pb
        ? (string?)pb.Attribute("id") : null, "a now after b");
    doc.Undo.Undo();
    Assert(a.El.PreviousNode is null, "z-order undone");

    doc.RemoveObject(a);
    Assert(doc.Objects.All(o => (string?)o.El.Attribute("id") != "a"), "deleted");
    doc.Undo.Undo();
    var restored = doc.Objects.First(o => (string?)o.El.Attribute("id") == "a");
    Assert(restored.El.PreviousNode is null, "restored at original z position");

    var grp = doc.Root.Descendants().First(e => (string?)e.Attribute("id") == "grp");
    doc.PromoteGroupToLayer(grp, "L2");
    AssertEq(2, doc.Layers.Count, "promoted");
    AssertEq("L2", doc.Layers[1].Name, "layer name");
    Assert(grp.Parent == doc.Root, "moved to top level");
    doc.Undo.Undo();
    AssertEq(1, doc.Layers.Count, "promotion undone");
    Assert(grp.Parent != doc.Root, "back in original parent");

    doc.Undo.Push(layer.SetLocked(true));
    Assert(doc.Layers[0].Locked, "layer locked");
    Assert(doc.HitTest(new(25, 5)) is null, "locked layer not hit-testable");
});

Check("editor: hit-test picks topmost, lines by distance, doc validates", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <g data-layer="L">
            <rect id="under" x="10" y="10" width="60" height="40"/>
            <rect id="over" x="30" y="20" width="60" height="40"/>
            <line id="ln" x1="0" y1="80" x2="288" y2="80" stroke-width="2"/>
            <text id="tx" x="100" y="90" font-size="10" text-anchor="middle">CENTERED</text>
          </g>
        </svg>
        """);
    AssertEq("over", (string?)doc.HitTest(new(40, 30))!.El.Attribute("id"), "topmost wins");
    AssertEq("under", (string?)doc.HitTest(new(15, 15))!.El.Attribute("id"), "non-overlap area");
    AssertEq("ln", (string?)doc.HitTest(new(150, 81))!.El.Attribute("id"), "line by distance");
    AssertEq("tx", (string?)doc.HitTest(new(100, 85))!.El.Attribute("id"), "anchored text box");
    Assert(doc.HitTest(new(5, 90)) is null, "empty space");
    Assert(doc.Validate().All(f => f.Severity != Etiq.Core.Severity.Error), "doc valid");
});

// ---------- editor: multi-select move, grouping, snapping ----------

Check("editor: multi-move is one undo entry, merges across drag", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <g data-layer="L">
            <rect id="a" x="10" y="10" width="20" height="10"/>
            <rect id="b" x="50" y="10" width="20" height="10"/>
          </g>
        </svg>
        """);
    var objs = doc.Objects;
    for (int i = 0; i < 4; i++) doc.MoveObjects(objs, 5, 2, "gmove:1");
    AssertEq(1, doc.Undo.Count, "drag = one entry");
    AssertEq(30.0, objs[0].GetNum("x"), "a moved");
    AssertEq(70.0, objs[1].GetNum("x"), "b moved");
    doc.Undo.Undo();
    AssertEq(10.0, objs[0].GetNum("x"), "a restored");
    AssertEq(50.0, objs[1].GetNum("x"), "b restored");
    doc.Undo.Redo();
    AssertEq(70.0, objs[1].GetNum("x"), "redo replays drag");
    doc.Undo.Undo();
    doc.RemoveObjects(doc.Objects);
    AssertEq(0, doc.Objects.Count, "multi-delete");
    doc.Undo.Undo();
    AssertEq(2, doc.Objects.Count, "multi-delete undone as one entry");
});

Check("editor: group/ungroup preserve z-position and undo cleanly", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <g data-layer="L">
            <rect id="under" x="0" y="0" width="10" height="10"/>
            <rect id="a" x="10" y="10" width="20" height="10"/>
            <rect id="b" x="50" y="10" width="20" height="10"/>
            <rect id="over" x="80" y="0" width="10" height="10"/>
          </g>
        </svg>
        """);
    string Order() => string.Join(",", doc.Layers[0].El.Descendants()
        .Where(e => (string?)e.Attribute("id") is not null)
        .Select(e => (string)e.Attribute("id")!));

    var toGroup = doc.Objects.Where(o =>
        (string?)o.El.Attribute("id") is "a" or "b").ToList();
    var g = doc.GroupObjects(toGroup);
    AssertEq("under,a,b,over", Order(), "grouping keeps document order");
    Assert(g.Parent == doc.Layers[0].El, "group sits in the layer at a's spot");
    Assert(Etiq.Editor.Core.EditorDoc.GroupContainer(toGroup[0].El) == g, "container found");
    AssertEq(2, doc.GroupMembers(toGroup[0]).Count, "click selects both members");

    doc.Undo.Undo();
    AssertEq("under,a,b,over", Order(), "ungrouped order restored");
    Assert(Etiq.Editor.Core.EditorDoc.GroupContainer(toGroup[0].El) is null, "no container after undo");

    doc.Undo.Redo();
    doc.Ungroup(g);
    AssertEq("under,a,b,over", Order(), "ungroup keeps order");
    Assert(toGroup[0].El.Parent == doc.Layers[0].El, "children back in layer");
    doc.Undo.Undo();
    Assert(toGroup[0].El.Parent == g, "ungroup undone: back in group");
});

Check("editor: snap engine aligns edges and centers within tolerance", () =>
{
    var others = new List<Etiq.Editor.Core.RectD> { new(100, 100, 50, 20) };
    // moving left edge at 103 → snap to other's left edge 100
    var (dx, dy, guides) = Etiq.Editor.Core.SnapEngine.Adjust(
        new(103, 200, 30, 10), others, null, 6);
    AssertEq(-3.0, dx, "left-edge snap");
    AssertEq(0.0, dy, "no y candidate in range");
    Assert(guides.Any(g => g.Vertical && g.Pos == 100), "vertical guide at 100");

    // centers align: moving center 126 vs other center 125
    (dx, _, _) = Etiq.Editor.Core.SnapEngine.Adjust(
        new(111, 200, 30, 10), others, null, 6);
    AssertEq(-1.0, dx, "center snap wins as nearest");

    // out of tolerance → no snap
    (dx, dy, guides) = Etiq.Editor.Core.SnapEngine.Adjust(
        new(300, 300, 30, 10), others, null, 6);
    Assert(dx == 0 && dy == 0 && guides.Count == 0, "no snap");

    // label edge snapping
    (_, dy, _) = Etiq.Editor.Core.SnapEngine.Adjust(
        new(5, 2, 30, 10), new List<Etiq.Editor.Core.RectD>(),
        new Etiq.Editor.Core.RectD(0, 0, 288, 96), 6);
    AssertEq(-2.0, dy, "snap to label top");
});

Check("editor: SnapPoint snaps a handle/endpoint per axis", () =>
{
    var others = new List<Etiq.Editor.Core.RectD> { new(100, 100, 50, 20) };
    // both axes: (103, 118) → other's left edge 100 / bottom edge 120
    var (p, guides) = Etiq.Editor.Core.SnapEngine.SnapPoint(
        new(103, 118), others, null, 6);
    AssertEq(100.0, p.X, "point x snaps to edge");
    AssertEq(120.0, p.Y, "point y snaps to edge");
    AssertEq(2, guides.Count, "guide per axis");

    // axis restriction: an N/S handle must not snap sideways
    (p, guides) = Etiq.Editor.Core.SnapEngine.SnapPoint(
        new(103, 118), others, null, 6, snapX: false);
    AssertEq(103.0, p.X, "x untouched when snapX off");
    AssertEq(120.0, p.Y, "y still snaps");
    Assert(guides.All(g => !g.Vertical), "no vertical guide");

    // out of tolerance → unchanged
    (p, guides) = Etiq.Editor.Core.SnapEngine.SnapPoint(
        new(300, 300), others, null, 6);
    Assert(p.X == 300 && p.Y == 300 && guides.Count == 0, "no snap when far");
});

Check("Code128: module patterns match the proven Go encoder", () =>
{
    // expected vectors generated from reference/labelprint/code128.go
    // (scan-verified in production) - the two encoders must stay in lockstep
    var cases = new (string Content, int Total, int[] Mods)[]
    {
        ("123456-1-1", 145, new[]{2,1,1,2,1,4,1,2,3,2,2,1,2,2,3,2,1,1,2,2,1,1,3,2,2,2,1,2,3,1,2,1,3,2,1,2,2,2,3,1,1,2,1,2,2,1,3,2,1,2,3,2,2,1,1,2,2,1,3,2,1,2,3,2,2,1,1,1,3,2,2,2,2,3,3,1,1,1,2}),
        ("WF-10245-B", 145, new[]{2,1,1,2,1,4,3,1,1,3,2,1,1,3,2,3,1,1,1,2,2,1,3,2,1,2,3,2,2,1,1,2,3,1,2,2,2,2,3,2,1,1,2,2,1,2,3,1,2,1,3,2,1,2,1,2,2,1,3,2,1,3,1,1,2,3,3,3,2,1,1,1,2,3,3,1,1,1,2}),
        ("500", 68, new[]{2,1,1,2,1,4,2,1,3,2,1,2,1,2,3,1,2,2,1,2,3,1,2,2,4,1,1,1,3,1,2,3,3,1,1,1,2}),
        ("1234", 57, new[]{2,1,1,2,3,2,1,1,2,2,3,2,1,3,1,1,2,3,1,2,1,2,4,1,2,3,3,1,1,1,2}), // even digits -> set C
        ("A", 46, new[]{2,1,1,2,1,4,1,1,1,3,2,3,1,3,1,1,2,3,2,3,3,1,1,1,2}),
    };
    foreach (var (content, total, mods) in cases)
    {
        var got = Etiq.Core.Code128.Modules(content);
        AssertEq(mods.Length, got.Length, $"{content}: element count");
        for (int i = 0; i < mods.Length; i++)
            AssertEq(mods[i], got[i], $"{content}: module {i}");
        AssertEq(total, Etiq.Core.Code128.TotalModules(content), $"{content}: total");
        Assert(got.Length % 2 == 1, $"{content}: starts and ends with a bar");
    }
    Assert(!Etiq.Core.Code128.CanEncode(""), "empty not encodable");
    Assert(!Etiq.Core.Code128.CanEncode("café"), "non-ASCII not encodable");
    try { Etiq.Core.Code128.Modules(""); Assert(false, "empty should throw"); }
    catch (FormatException) { }
});

Check("Code39: module patterns match the zbar-verified table", () =>
{
    // expected vectors from the encoding table verified by scanning
    // rendered symbols with zbar (full base character set decoded)
    var a = Etiq.Core.Code39.Modules("A");
    AssertEq(29, a.Length, "A: element count");
    AssertEq(47, Etiq.Core.Code39.TotalModules("A"), "A: total modules");
    var expA = new[]{1,3,1,1,3,1,3,1,1,1,3,1,1,1,1,3,1,1,3,1,1,3,1,1,3,1,3,1,1};
    for (int i = 0; i < expA.Length; i++) AssertEq(expA[i], a[i], $"A: module {i}");

    AssertEq(143, Etiq.Core.Code39.TotalModules("CID-042"), "CID-042: total");
    Assert(Etiq.Core.Code39.Modules("cid").Length ==
           Etiq.Core.Code39.Modules("CID").Length, "base set upper-cases lowercase");
    Assert(!Etiq.Core.Code39.CanEncode("a*b"), "star not encodable as content");
    Assert(!Etiq.Core.Code39.CanEncode("café"), "non-ASCII not encodable");
    // extended: lowercase maps to +SHIFT pairs (one extra char + gap = 20 modules... element count grows)
    Assert(Etiq.Core.Code39.CanEncode("Mix_ed!", extended: true), "extended full ASCII");
    Assert(Etiq.Core.Code39.Modules("a", extended: true).Length >
           Etiq.Core.Code39.Modules("A").Length, "extended shift pair is longer");
    foreach (var m in new[] { Etiq.Core.Code39.Modules("XYZ"), Etiq.Core.Code39.Modules("0") })
        Assert(m.Length % 2 == 1, "starts and ends with a bar");
});

Check("compose: newline segs, smart sep, collapse-blank-lines", () =>
{
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Name" source="fixed" value="Dudek"/>
            <etiq:field name="Addr2" source="prompt"/>
            <etiq:field name="City" source="fixed" value="Chicago"/>
            <etiq:field name="State" source="prompt"/>
            <etiq:field name="Zip" source="fixed" value="60644"/>
            <etiq:field name="Block" source="compose" collapse-blank-lines="true">
              <etiq:seg ref="Name"/>
              <etiq:seg newline="true"/>
              <etiq:seg ref="Addr2"/>
              <etiq:seg newline="true"/>
              <etiq:seg ref="City"/>
              <etiq:seg ref="State" sep=", "/>
              <etiq:seg ref="Zip" sep=" "/>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Block">x</text></g>
        </svg>
        """);
    Assert(TemplateValidator.Validate(t).Count(f => f.Severity == Severity.Error) == 0,
        "newline/sep/collapse validate clean");
    var ctx = new ResolveContext
    {
        PromptValues = new() { ["Addr2"] = "", ["State"] = "IL" },
        EpicorColumn = _ => null,
    };
    AssertEq("Dudek\nChicago, IL 60644",
        new FieldResolver(t, ctx).Resolve("Block"), "blank Addr2 line collapses; seps join");

    // blank State: its sep vanishes with it, Zip's sep still joins City+Zip
    ctx = new ResolveContext
    {
        PromptValues = new() { ["Addr2"] = "Suite 9", ["State"] = "" },
        EpicorColumn = _ => null,
    };
    AssertEq("Dudek\nSuite 9\nChicago 60644",
        new FieldResolver(t, ctx).Resolve("Block"), "no dangling comma for blank State");

    // sep at line start suppressed (empty City case)
    var t2 = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="A" source="prompt"/>
            <etiq:field name="B" source="fixed" value="IL"/>
            <etiq:field name="C" source="compose">
              <etiq:seg ref="A"/><etiq:seg ref="B" sep=", "/>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="C">x</text></g>
        </svg>
        """);
    var r2 = new FieldResolver(t2, new ResolveContext
        { PromptValues = new() { ["A"] = "" }, EpicorColumn = _ => null });
    AssertEq("IL", r2.Resolve("C"), "sep suppressed at line start");

    // validator: newline seg must be alone; collapse only on compose
    var bad = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="P" source="prompt" collapse-blank-lines="true"/>
            <etiq:field name="Q" source="compose">
              <etiq:seg newline="true" value="x"/>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Q">x</text></g>
        </svg>
        """);
    var findings = TemplateValidator.Validate(bad);
    Assert(findings.Any(f => f.Code == "compose-collapse"), "collapse on non-compose flagged");
    Assert(findings.Any(f => f.Code == "seg-newline"), "newline seg with extras flagged");
});

Check("snippets: load, collision rename + ref rewrite, package transitive", () =>
{
    // load the SHIPPED snippets (repo /snippets) and materialize into a
    // template that already uses one of the names
    var snips = SnippetLibrary.Load("snippets");
    Assert(snips.Count >= 2, "shipped snippets found");
    var intl = snips.First(s => s.Name.Contains("International"));

    var taken = new HashSet<string> { "AddressBlock", "AddrCity" };
    var els = SnippetLibrary.Materialize(intl, taken.Contains);
    var byName = els.ToDictionary(e => (string?)e.Attribute("name") ?? "");
    Assert(byName.ContainsKey("AddressBlock2"), "colliding compose renamed");
    Assert(byName.ContainsKey("AddrCity2"), "colliding helper renamed");
    Assert(byName.ContainsKey("AddrStreet"), "non-colliding name kept");
    var compose = byName["AddressBlock2"];
    var ns = EtiqTemplate.Ns;
    Assert(compose.Descendants(ns + "seg")
        .Any(s => (string?)s.Attribute("ref") == "AddrCity2"), "internal refs rewritten");
    Assert(!compose.Descendants(ns + "seg")
        .Any(s => (string?)s.Attribute("ref") == "AddrCity"), "no stale refs");

    // materialized bundle actually resolves (drop it into a template)
    var label = new System.Xml.Linq.XElement(ns + "label");
    foreach (var e in els) label.Add(e);
    var svg = new System.Xml.Linq.XElement(EtiqTemplate.Svg + "svg",
        new System.Xml.Linq.XAttribute("width", "6in"),
        new System.Xml.Linq.XAttribute("height", "4in"),
        new System.Xml.Linq.XAttribute("viewBox", "0 0 6000 4000"),
        new System.Xml.Linq.XElement(EtiqTemplate.Svg + "metadata", label),
        new System.Xml.Linq.XElement(EtiqTemplate.Svg + "g",
            new System.Xml.Linq.XAttribute("data-layer", "F"),
            new System.Xml.Linq.XElement(EtiqTemplate.Svg + "text",
                new System.Xml.Linq.XAttribute("x", "1"),
                new System.Xml.Linq.XAttribute("y", "2"),
                new System.Xml.Linq.XAttribute("data-field", "AddressBlock2"), "x")));
    var tpl = EtiqTemplate.Parse(svg.ToString());
    var r = new FieldResolver(tpl, new ResolveContext
    {
        PromptValues = new()
        {
            ["AddrCountry"] = "DE", ["AddrStreet"] = "Hauptstr. 5", ["AddrStreet2"] = "",
            ["AddrCity2"] = "Koeln", ["AddrState"] = "", ["AddrZip"] = "50667",
        },
        EpicorColumn = _ => null,
    });
    AssertEq("Hauptstr. 5\n50667 Koeln\nGERMANY", r.Resolve("AddressBlock2"),
        "materialized snippet resolves with DE ordering");

    // package: field + transitive refs round-trip
    var packaged = SnippetLibrary.Package("My Address", label.Element(ns + "field")!
        .Parent!.Elements(ns + "field")
        .First(f => (string?)f.Attribute("name") == "AddressBlock2"), label);
    Assert(packaged.Elements(ns + "field").Count() >= 6, "helpers packaged transitively");
});

Check("compose variants: switch-on picks the segment list (intl addresses)", () =>
{
    var tpl = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Country" source="prompt" caption="Country:"/>
            <etiq:field name="Street" source="prompt" caption="Street:"/>
            <etiq:field name="City" source="prompt" caption="City:"/>
            <etiq:field name="State" source="prompt" caption="State:"/>
            <etiq:field name="Zip" source="prompt" caption="Zip:"/>
            <etiq:field name="Addr" source="compose" switch-on="Country" collapse-blank-lines="true">
              <etiq:variant when="DE">
                <etiq:seg ref="Street"/>
                <etiq:seg newline="true"/>
                <etiq:seg ref="Zip"/>
                <etiq:seg ref="City" sep=" "/>
              </etiq:variant>
              <etiq:variant prefix="GB">
                <etiq:seg ref="Street"/>
                <etiq:seg newline="true"/>
                <etiq:seg ref="City"/>
                <etiq:seg newline="true"/>
                <etiq:seg ref="Zip"/>
              </etiq:variant>
              <etiq:variant>
                <etiq:seg ref="Street"/>
                <etiq:seg newline="true"/>
                <etiq:seg ref="City"/>
                <etiq:seg ref="State" sep=", "/>
                <etiq:seg ref="Zip" sep=" "/>
              </etiq:variant>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Addr">x</text></g>
        </svg>
        """);
    Assert(TemplateValidator.Validate(tpl).Count(f => f.Severity == Severity.Error) == 0,
        "variant template validates clean");
    string Run(string country) => new FieldResolver(tpl, new ResolveContext
    {
        PromptValues = new()
        {
            ["Country"] = country, ["Street"] = "Hauptstr. 5",
            ["City"] = "Koeln", ["State"] = "", ["Zip"] = "50667",
        },
        EpicorColumn = _ => null,
    }).Resolve("Addr");
    AssertEq("Hauptstr. 5\n50667 Koeln", Run("DE"), "German order: zip before city");
    AssertEq("Hauptstr. 5\nKoeln\n50667", Run("GB-ENG"), "prefix match");
    AssertEq("Hauptstr. 5\nKoeln 50667", Run("US"), "default variant + blank State collapses");

    // validator: variants need switch-on; mixed segs+variants flagged; no default warns
    var bad = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="A" source="prompt" caption="a"/>
            <etiq:field name="B" source="compose">
              <etiq:seg ref="A"/>
              <etiq:variant when="x"><etiq:seg ref="A"/></etiq:variant>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="B">x</text></g>
        </svg>
        """);
    var findings = TemplateValidator.Validate(bad);
    Assert(findings.Any(f => f.Code == "variant-mixed"), "mixed segs+variants flagged");
    Assert(findings.Any(f => f.Code == "variant-switch"), "missing switch-on flagged");
    Assert(findings.Any(f => f.Code == "variant-default" && f.Severity == Severity.Warning),
        "no default variant warns");
});

Check("variants: multi-value when + switch on a normalizing compose helper", () =>
{
    // the comprehensive-address pattern: raw country -> map -> code,
    // block switches on the code, one variant covers a format GROUP
    var tpl = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Country" source="prompt" caption="Country:"/>
            <etiq:field name="CC" source="compose">
              <etiq:seg ref="Country" case="upper" map="Codes"/>
            </etiq:field>
            <etiq:map name="Codes" default="XX">
              <etiq:when from="DE" to="DE"/>
              <etiq:when from="GERMANY" to="DE"/>
              <etiq:when from="AT" to="AT"/>
              <etiq:when from="AUSTRIA" to="AT"/>
            </etiq:map>
            <etiq:field name="City" source="prompt" caption="City:"/>
            <etiq:field name="Zip" source="prompt" caption="Zip:"/>
            <etiq:field name="Addr" source="compose" switch-on="CC">
              <etiq:variant when="DE|AT">
                <etiq:seg ref="Zip"/>
                <etiq:seg ref="City" sep=" "/>
              </etiq:variant>
              <etiq:variant>
                <etiq:seg ref="City"/>
                <etiq:seg ref="Zip" sep=" "/>
              </etiq:variant>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Addr">x</text></g>
        </svg>
        """);
    Assert(TemplateValidator.Validate(tpl).Count(f => f.Severity == Severity.Error) == 0,
        "switch-on a plain compose helper validates clean");
    string Run(string country) => new FieldResolver(tpl, new ResolveContext
    {
        PromptValues = new() { ["Country"] = country, ["City"] = "Wien", ["Zip"] = "1010" },
        EpicorColumn = _ => null,
    }).Resolve("Addr");
    AssertEq("1010 Wien", Run("Austria"), "name normalized by helper, multi-when matched");
    AssertEq("1010 Wien", Run("at"), "lowercase code normalized");
    AssertEq("Wien 1010", Run("Atlantis"), "unknown -> XX -> default variant");

    // chained switching is flagged; circular refs block instead of overflowing
    var chained = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="A" source="prompt" caption="a"/>
            <etiq:field name="B" source="compose" switch-on="A">
              <etiq:variant><etiq:seg value="x"/></etiq:variant>
            </etiq:field>
            <etiq:field name="C" source="compose" switch-on="B">
              <etiq:variant><etiq:seg value="y"/></etiq:variant>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="C">x</text></g>
        </svg>
        """);
    Assert(TemplateValidator.Validate(chained).Any(
            f => f.Code == "variant-switch" && f.Message.Contains("chained")),
        "switch-on a switching field flagged");
    var circular = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="A" source="compose" switch-on="A">
              <etiq:variant><etiq:seg value="x"/></etiq:variant>
            </etiq:field>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="A">x</text></g>
        </svg>
        """);
    try
    {
        new FieldResolver(circular, new ResolveContext()).Resolve("A");
        Assert(false, "circular reference should throw");
    }
    catch (ResolveException ex)
    {
        Assert(ex.Message.Contains("circular"), "circular reference reported, not overflowed");
    }
});

Check("shipped comprehensive intl snippet: groups, aliases, unknowns", () =>
{
    var snips = SnippetLibrary.Load("snippets");
    var intl = snips.First(s => s.Name.Contains("International"));
    var ns = EtiqTemplate.Ns;
    var label = new XElement(ns + "label",
        new XAttribute(XNamespace.Xmlns + "etiq", ns.NamespaceName));
    foreach (var el in SnippetLibrary.Materialize(intl, _ => false)) label.Add(el);
    var svg = new XElement(XNamespace.Get("http://www.w3.org/2000/svg") + "svg",
        new XAttribute("width", "6in"), new XAttribute("height", "4in"),
        new XAttribute("viewBox", "0 0 6000 4000"),
        new XElement(XNamespace.Get("http://www.w3.org/2000/svg") + "metadata", label));
    var tpl = EtiqTemplate.Parse(svg.ToString());
    var snipErrs = TemplateValidator.Validate(tpl)
        .Where(f => f.Severity == Severity.Error).ToList();
    Assert(snipErrs.Count == 0,
        "shipped intl snippet has no validation errors: " + string.Join(" | ", snipErrs));
    string Run(string country, string state = "IL") => new FieldResolver(tpl, new ResolveContext
    {
        PromptValues = new()
        {
            ["AddrCountry"] = country, ["AddrStreet"] = "1 Main St", ["AddrStreet2"] = "",
            ["AddrCity"] = "Springfield", ["AddrState"] = state, ["AddrZip"] = "62701",
        },
        EpicorColumn = _ => null,
    }).Resolve("AddressBlock");
    AssertEq("1 Main St\nSpringfield, IL 62701", Run(""), "blank country = US domestic, no country line");
    AssertEq("1 Main St\nSpringfield, IL 62701", Run("usa"), "USA alias = US domestic");
    AssertEq("1 Main St\n62701 Springfield\nGERMANY", Run("Deutschland", ""),
        "German alias -> zip-city group + mailing name");
    AssertEq("1 Main St\nSpringfield\n62701\nUNITED KINGDOM", Run("England", ""),
        "England -> GB group, postcode own line");
    AssertEq("1 Main St\nSpringfield ON 62701\nCANADA", Run("Canada", "ON"),
        "CA group: city prov postal, no comma");
    AssertEq("1 Main St\nSpringfield 62701\nJAPAN", Run("Japan", ""),
        "JP group: city zip");
    AssertEq("1 Main St\nSpringfield 62701\nATLANTIS", Run("Atlantis", ""),
        "unknown country prints as typed, default ordering");
});

Check("BatchRunner: ListSelections survive into per-label contexts", () =>
{
    var tpl = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Who" source="list" list="L" column="Name"/>
            <etiq:list name="L" key="K" default="a">
              <etiq:row K="a" Name="Alpha"/>
              <etiq:row K="b" Name="Beta"/>
            </etiq:list>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Who">x</text></g>
        </svg>
        """);
    var ctx = new ResolveContext
    {
        ListSelections = new() { ["L"] = "b" },
        EpicorColumn = _ => null,
    };
    var labels = BatchRunner.Run(tpl,
        new List<Dictionary<string, string>> { new(StringComparer.OrdinalIgnoreCase) }, ctx);
    AssertEq("Beta", labels[0].Fields["Who"],
        "--choose selection must not fall back to the list default in batch runs");
});

Check("editor: RotateObjects preserves group layout (rotation composition)", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <rect id="a" x="0" y="0" width="10" height="10"/>
            <rect id="b" x="100" y="0" width="10" height="10"/>
          </g>
        </svg>
        """);
    var objs = doc.Objects;
    // rotate the pair 90 cw about their combined center (55,5)
    doc.RotateObjects(objs, 90, new(55, 5));
    var ca = objs[0].WorldBounds().Center;
    var cb = objs[1].WorldBounds().Center;
    AssertEq(55.0, Math.Round(ca.X, 3), "a center x");
    AssertEq(-45.0, Math.Round(ca.Y, 3), "a center y");
    AssertEq(55.0, Math.Round(cb.X, 3), "b center x");
    AssertEq(55.0, Math.Round(cb.Y, 3), "b center y");
    double dist = Math.Sqrt(Math.Pow(cb.X - ca.X, 2) + Math.Pow(cb.Y - ca.Y, 2));
    AssertEq(100.0, Math.Round(dist, 3), "spacing preserved");
    AssertEq(1, doc.Undo.Count, "one undo entry for the whole group");
    doc.Undo.Undo();
    AssertEq(5.0, Math.Round(objs[0].WorldBounds().Center.X, 3), "undo restores");

    // composition: an already-rotated member composes into ONE rotation
    var d2 = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <rect id="r" x="0" y="0" width="10" height="6" transform="rotate(90 0 0)"/>
          </g>
        </svg>
        """);
    var r = d2.Objects[0];
    d2.RotateObjects(new[] { r }, 90, new Etiq.Editor.Core.PointD(10, 0));
    AssertEq("rotate(180 5 -5)", (string?)r.El.Attribute("transform"), "composed pivot solved");

    // cancellation: 180 + 180 degenerates to a plain move, transform cleared
    d2.RotateObjects(new[] { r }, 180, new Etiq.Editor.Core.PointD(0, 0));
    Assert(r.El.Attribute("transform") is null, "angles cancel -> transform cleared");
});

Check("editor: SplitMultiline makes an SVG-pure line stack (data-line when bound)", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <text id="t" x="100" y="500" font-size="200" data-line-height="250"
                  data-field="Block" data-valign="middle" data-height="900">A&#10;B&#10;C</text>
          </g>
        </svg>
        """);
    var t = doc.Objects.First();
    doc.Undo.Push(t.SplitMultiline());
    var texts = doc.Objects.Where(o => o.Kind == Etiq.Editor.Core.ObjectKind.Text).ToList();
    AssertEq(3, texts.Count, "one element per line");
    AssertEq("A", texts[0].El.Value, "line content split");
    AssertEq("0", (string?)texts[0].El.Attribute("data-line"), "bound stack indexes lines");
    AssertEq("2", (string?)texts[2].El.Attribute("data-line"), "0-based");
    AssertEq(500.0, texts[0].GetNum("y"), "first baseline kept");
    AssertEq(1000.0, texts[2].GetNum("y"), "spaced by line-height");
    Assert(texts[1].El.Attribute("data-height") is null &&
           texts[1].El.Attribute("data-valign") is null &&
           texts[1].El.Attribute("data-line-height") is null,
           "block-level attrs dropped per line");
    Assert(texts[0].El.Parent!.Name.LocalName == "g", "stack is grouped");
    doc.Undo.Undo();
    AssertEq(1, doc.Objects.Count(o => o.Kind == Etiq.Editor.Core.ObjectKind.Text),
        "undo restores the single element");
});

Check("editor: multiline text bounds use line-height and data-height box", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <text id="t" x="100" y="500" font-size="200">Line one&#10;Line two longer</text>
          </g>
        </svg>
        """);
    var t = doc.Objects.First();
    var b = t.Bounds();
    AssertEq(440.0, Math.Round(b.H), "h = size + lineH (1.2em default)");
    // data-height taller than the block wins (valign room)
    doc.Undo.Push(t.SetAttr("data-height", "900", "box"));
    AssertEq(900.0, Math.Round(t.Bounds().H), "data-height box drives bounds");
    // with a height box, a vertical drag sizes the box, not the font
    doc.Undo.Push(t.Resize(new(b.X, b.Y, b.W, 700)));
    AssertEq("700", (string?)t.El.Attribute("data-height"), "drag resizes the box");
    AssertEq("200", (string?)t.El.Attribute("font-size") ?? "200", "font untouched");
});

Check("editor: text resize writes only the axis that moved", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <text id="t" x="100" y="500" font-size="200">Hello</text>
          </g>
        </svg>
        """);
    var t = doc.Objects.First();
    var b = t.Bounds();   // top = 500 - 160 = 340, h = 200

    // height-only change scales the font and keeps the baseline consistent;
    // it must NOT lock in a data-width
    doc.Undo.Push(t.Resize(new(b.X, b.Y, b.W, 300)));
    AssertEq("300", (string?)t.El.Attribute("font-size"), "font scales with height");
    Assert(t.El.Attribute("data-width") is null, "no data-width from a vertical drag");
    AssertEq(580.0, t.GetNum("y"), "baseline = top + 0.8em");

    // width-only change sets the shrink box, font untouched
    var b2 = t.Bounds();
    doc.Undo.Push(t.Resize(new(b2.X, b2.Y, 1234, b2.H)));
    AssertEq("1234", (string?)t.El.Attribute("data-width"), "data-width from horizontal drag");
    AssertEq("300", (string?)t.El.Attribute("font-size"), "font unchanged");
});

Check("editor: fit modes + top-edge drag on a data-height box", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <text id="a" x="100" y="500" font-size="200">A</text>
            <text id="b" x="100" y="500" font-size="200" data-width="900">B</text>
            <text id="c" x="100" y="500" font-size="200" data-width="900"
                  data-height="240" data-fit="box">C</text>
            <text id="d" x="100" y="500" font-size="200" data-width="900"
                  data-height="900">D</text>
          </g>
        </svg>
        """);
    var objs = doc.Objects.ToDictionary(o => (string?)o.El.Attribute("id") ?? "");
    AssertEq("none", objs["a"].FitMode, "no boxes -> dynamic width");
    AssertEq("width", objs["b"].FitMode, "data-width -> width fit inferred");
    AssertEq("box", objs["c"].FitMode, "explicit data-fit=box");
    AssertEq("width", objs["d"].FitMode, "box fit is never inferred (valign boxes keep behavior)");
    AssertEq(240.0, objs["c"].Bounds().H, "box fit: bounds height IS the box");
    AssertEq(900.0, objs["d"].Bounds().H, "valign box: height is max(content, box)");

    // REGRESSION: dragging the TOP edge of a data-height text must move the
    // top (y follows), not grow the box downward past the fixed bottom edge
    var t = objs["d"];
    var b = t.Bounds();               // top = 340, bottom = 1240
    double bottom = b.Bottom;
    doc.Undo.Push(t.Resize(new(b.X, b.Y - 100, b.W, b.H + 100)));   // pull top up 100
    var nb = t.Bounds();
    AssertEq(b.Y - 100, nb.Y, "top edge follows the drag");
    AssertEq(bottom, nb.Bottom, "bottom edge stays put");
    AssertEq("1000", (string?)t.El.Attribute("data-height"), "box height grew by the drag");

    // explicit data-fit=none LOCKS the font: a vertical drag creates/sizes
    // the clip box and never rescales font-size
    var doc2 = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <text x="100" y="500" font-size="200" data-fit="none">E</text>
          </g>
        </svg>
        """);
    var e = doc2.Objects.First();
    var eb = e.Bounds();
    doc2.Undo.Push(e.Resize(new(eb.X, eb.Y, eb.W, eb.H + 150)));
    AssertEq("200", (string?)e.El.Attribute("font-size") ?? "200", "fit=none: font untouched by vertical drag");
    AssertEq(N0(eb.H + 150), (string?)e.El.Attribute("data-height"), "fit=none: drag sizes the clip box");
    static string N0(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
});

Check("list picker attrs: caption/display/filter parse + validate", () =>
{
    var tpl = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Region" source="prompt" caption="Region:"/>
            <etiq:field name="Who" source="list" list="L" column="Name"/>
            <etiq:field name="Pick" source="compose">
              <etiq:seg ref="Who"/>
            </etiq:field>
            <etiq:list name="L" key="K" caption="Customer:" display="Pick"
                       filter-column="Region" filter-ref="Region">
              <etiq:row K="a" Name="Alpha" Region="east"/>
              <etiq:row K="b" Name="Beta" Region="west"/>
            </etiq:list>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Pick">x</text></g>
        </svg>
        """);
    var l = tpl.Lists.Single();
    AssertEq("Customer:", l.Caption, "caption parsed");
    AssertEq("Pick", l.Display, "display parsed");
    AssertEq("Region", l.FilterColumn, "filter-column parsed");
    AssertEq("Region", l.FilterRef, "filter-ref parsed");
    Assert(TemplateValidator.Validate(tpl).Count(f => f.Severity == Severity.Error) == 0,
        "well-formed picker attrs validate clean");

    var bad = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Who" source="list" list="L" column="Name"/>
            <etiq:list name="L" key="K" display="Nope" filter-column="Region">
              <etiq:row K="a" Name="Alpha"/>
            </etiq:list>
          </etiq:label></metadata>
          <g data-layer="F">
            <text x="1" y="2" data-field="Who" data-fit="sideways">x</text>
            <text x="1" y="2" data-width="500" data-fit="box">y</text>
          </g>
        </svg>
        """);
    var findings = TemplateValidator.Validate(bad);
    Assert(findings.Any(f => f.Code == "list-display"), "unknown display field flagged");
    Assert(findings.Any(f => f.Code == "list-filter"), "filter-column without filter-ref flagged");
    Assert(findings.Any(f => f.Code == "text-fit" && f.Message.Contains("sideways")),
        "bad data-fit value flagged");
    Assert(findings.Any(f => f.Code == "text-fit" && f.Severity == Severity.Warning
                          && f.Message.Contains("data-fit=\"box\"")),
        "box fit without a height box warns");

    var circular = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Who" source="list" list="L" column="Name"/>
            <etiq:list name="L" key="K" filter-column="Name" filter-ref="Who">
              <etiq:row K="a" Name="Alpha"/>
            </etiq:list>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Who">x</text></g>
        </svg>
        """);
    Assert(TemplateValidator.Validate(circular).Any(
            f => f.Code == "list-filter" && f.Message.Contains("circular")),
        "filter-ref reading its own list flagged");

    // columns= carries the editor's column ORDER (rows are sparse, so
    // attribute-derived order reshuffles after a reorder — the bug)
    var ordered = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Who" source="list" list="L" column="Name"/>
            <etiq:list name="L" key="K" columns="K,Addr2,Name">
              <etiq:row K="a" Name="Alpha"/>
              <etiq:row K="b" Name="Beta" Addr2="Suite 9"/>
            </etiq:list>
            <etiq:list name="M" key="K" columns="Name">
              <etiq:row K="a" Name="Alpha"/>
            </etiq:list>
          </etiq:label></metadata>
          <g data-layer="F"><text x="1" y="2" data-field="Who">x</text></g>
        </svg>
        """);
    Assert(ordered.Lists.First(x => x.Name == "L").Columns is ["K", "Addr2", "Name"],
        "columns= parsed in declared order");
    Assert(TemplateValidator.Validate(ordered).Any(
            f => f.Code == "list-columns" && f.Severity == Severity.Warning),
        "columns= missing the key column warns");
});

Check("editor: doc metadata etiq:label get-or-create + undoable replace", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L"><text x="1" y="2">t</text></g>
        </svg>
        """);
    Assert(doc.EtiqLabel() is null, "no metadata yet");
    var created = doc.GetOrCreateEtiqLabelClone();
    AssertEq("label", created.Name.LocalName, "clone is etiq:label");
    created.Add(new System.Xml.Linq.XElement(
        Etiq.Editor.Core.EditorDoc.EtiqNs + "field",
        new System.Xml.Linq.XAttribute("name", "F1"),
        new System.Xml.Linq.XAttribute("source", "prompt")));
    doc.Undo.Push(doc.ReplaceEtiqLabel(created));
    Assert(doc.EtiqLabel() is not null, "label installed");
    Assert(doc.Xml.ToString().Contains("F1"), "field persisted");
    doc.Undo.Undo();
    Assert(doc.EtiqLabel() is null, "undo removes created label");
    doc.Undo.Redo();
    Assert(doc.Xml.ToString().Contains("F1"), "redo restores");

    // replace an EXISTING label keeps undo symmetric
    var edited = doc.GetOrCreateEtiqLabelClone();
    System.Xml.Linq.Extensions.Remove(edited.Elements());
    doc.Undo.Push(doc.ReplaceEtiqLabel(edited));
    Assert(!doc.Xml.ToString().Contains("F1"), "replacement applied");
    doc.Undo.Undo();
    Assert(doc.Xml.ToString().Contains("F1"), "undo restores previous fields");
});

Check("editor: WorldBounds is the rotated AABB; line endpoint edit is undoable", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="L">
            <rect id="bc" x="1000" y="500" width="400" height="100"
                  transform="rotate(90 1000 500)" data-barcode="code128"/>
            <line id="ln" x1="100" y1="200" x2="300" y2="200" stroke-width="2"/>
          </g>
        </svg>
        """);
    var bc = doc.Objects.First(o => (string?)o.El.Attribute("id") == "bc");
    var wb = bc.WorldBounds();
    // rect rotated 90cw about its own top-left: spans x 900..1000, y 500..900
    AssertEq(900.0, Math.Round(wb.X), "world left");
    AssertEq(500.0, Math.Round(wb.Y), "world top");
    AssertEq(100.0, Math.Round(wb.W), "world w = rect h");
    AssertEq(400.0, Math.Round(wb.H), "world h = rect w");

    var ln = doc.Objects.First(o => (string?)o.El.Attribute("id") == "ln");
    doc.Undo.Push(ln.SetLineEndpoint(2, new(350, 250)));
    AssertEq("350", (string?)ln.El.Attribute("x2"), "x2 updated");
    AssertEq("250", (string?)ln.El.Attribute("y2"), "y2 updated");
    doc.Undo.Push(ln.SetLineEndpoint(2, new(360, 260))); // merges
    doc.Undo.Undo();
    AssertEq("300", (string?)ln.El.Attribute("x2"), "merged drag undoes to original");
    AssertEq("200", (string?)ln.El.Attribute("y2"), "y2 back");
});

// ---------- rest connection profiles + pick evaluator ----------

Check("JsonPick: dotted path with index, misses return null", () =>
{
    using var doc = JsonDocument.Parse("""
        {"assets":[{"name":"PC-042","location":{"city":"CHICAGO"}},{"name":"PC-043"}],
         "count":2,"tag":null}
        """);
    var root = doc.RootElement;
    AssertEq("PC-042", JsonPick.Evaluate(root, "assets[0].name")!, "index + prop");
    AssertEq("CHICAGO", JsonPick.Evaluate(root, "assets[0].location.city")!, "nested");
    AssertEq("2", JsonPick.Evaluate(root, "count")!, "number to string");
    Assert(JsonPick.Evaluate(root, "assets[9].name") is null, "index out of range");
    Assert(JsonPick.Evaluate(root, "nope.x") is null, "missing prop");
    Assert(JsonPick.Evaluate(root, "tag") is null, "null value");
});

Check("ConnectionProfiles: parse + dup/empty rejection", () =>
{
    var profiles = ConnectionProfiles.Parse("""
        [{"name":"glpi","kind":"glpi","baseUrl":"https://g/apirest.php/",
          "appTokenSecret":"APP","userTokenSecret":"USR"},
         {"name":"anon","kind":"none","baseUrl":"https://a/"}]
        """);
    AssertEq(2, profiles.Count, "count");
    AssertEq("glpi", profiles["GLPI"].Kind, "case-insensitive lookup");
    try { ConnectionProfiles.Parse("""[{"name":"","baseUrl":"x"}]"""); throw new Exception("empty name accepted"); }
    catch (InvalidDataException) { }
});

Check("RestClient: GLPI session dance + pick via mock", () =>
{
    int calls = 0;
    var handler = new MockHandler(req =>
    {
        calls++;
        if (req.RequestUri!.AbsolutePath.EndsWith("/initSession"))
        {
            Assert(req.Headers.TryGetValues("App-Token", out var a) && a.First() == "APP", "app token");
            Assert(req.Headers.TryGetValues("Authorization", out var u) && u.First() == "user_token USR", "user token");
            return Json("""{"session_token":"SESS-1"}""");
        }
        Assert(req.Headers.TryGetValues("Session-Token", out var s) && s.First() == "SESS-1", "session header");
        Assert(req.RequestUri.PathAndQuery.Contains("Computer/42"), "query path");
        return Json("""{"id":42,"name":"PC-042","locations_id":{"name":"CHICAGO"}}""");
    });
    using var c = new RestClient(new ConnectionProfile
    {
        Name = "glpi", Kind = "glpi", BaseUrl = "https://g/apirest.php/",
        AppTokenSecret = "APP", UserTokenSecret = "USR",
    }, handler);
    AssertEq("PC-042", c.Fetch("Computer/42", "name")!, "fetched + picked");
    AssertEq("CHICAGO", c.Fetch("Computer/42", "locations_id.name")!, "nested pick");
    AssertEq(3, calls, "one initSession + two fetches (session cached)");
});

Check("RestClient plugs into FieldResolver as the rest provider", () =>
{
    var handler = new MockHandler(_ => Json("""{"name":"PC-042"}"""));
    using var client = new RestClient(new ConnectionProfile
        { Name = "glpi", Kind = "none", BaseUrl = "https://g/" }, handler);
    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="3in" height="1in" viewBox="0 0 288 96">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="Asset" source="rest" connection="glpi"
                        query="Computer/42" pick="name"/>
          </etiq:label></metadata>
          <text x="8" y="20" data-field="Asset">p</text>
        </svg>
        """);
    var r = new FieldResolver(t, new ResolveContext
    {
        Rest = (conn, q, pick) => conn == "glpi" ? client.Fetch(q, pick) : null,
    });
    AssertEq("PC-042", r.Resolve("Asset"), "end to end");
});

// ---------- printer/media registry + feasibility ----------

Check("Registry load + feasibility: fit, module dots, snapping", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "etiq-reg-test");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "printers.json"), """
        [{"name":"ZT230","dpi":203,"widthMils":4090,"path":"zpl"},
         {"name":"PT-D410","dpi":180,"widthMils":708,"path":"driver"}]
        """);
    File.WriteAllText(Path.Combine(dir, "media.json"), """
        [{"name":"6x4","kind":"diecut","widthMils":6000,"heightMils":4000}]
        """);
    var reg = Registry.Load(dir);
    AssertEq(2, reg.Printers.Count, "printers");
    AssertEq(1, reg.Media.Count, "media");

    var t = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 576 384">
          <rect x="10" y="10" width="200" height="60" data-barcode="code128"
                data-value="X" data-module-mils="13"/>
          <rect x="10" y="100" width="200" height="60" data-barcode="code39"
                data-value="X" data-module-mils="3"/>
        </svg>
        """);
    var zt = FeasibilityChecker.Check(t, reg.Printers["ZT230"]);
    Assert(zt.Any(f => f.Code == "printer-fit" && f.Severity == Severity.Warning &&
                       f.Message.Contains("rotated")), "6x4 on ZT230 = rotated-feed warning");
    Assert(!zt.Any(f => f.Code == "printer-fit" && f.Severity == Severity.Error),
           "6x4 fits ZT230 rotated");
    Assert(zt.Any(f => f.Code == "module-dots" && f.Severity == Severity.Error &&
                       f.Message.Contains("3 mils")), "3-mil module impossible at 203dpi");
    Assert(!zt.Any(f => f.Code == "module-dots" && f.Message.Contains("13 mils") &&
                        f.Severity == Severity.Error), "13-mil module fine");

    var pt = FeasibilityChecker.Check(t, reg.Printers["PT-D410"]);
    Assert(pt.Any(f => f.Code == "printer-fit" && f.Severity == Severity.Error),
           "6in label cannot print on 18mm tape printer");

    AssertEq(6000, FeasibilityChecker.PhysicalMils("6in")!.Value, "in→mils");
    AssertEq(709, FeasibilityChecker.PhysicalMils("18mm")!.Value, "mm→mils");
    Directory.Delete(dir, recursive: true);
});

// ---------- BtwFile on synthetic fixture bytes ----------

#if ETIQ_BTW
Check("BtwFile parses synthetic container", () =>
{
    byte[] data = BuildSyntheticBtw();
    var btw = BtwFile.Parse(data);
    AssertEq("10.1 SR3", btw.Version!, "version");
    AssertEq("2954", btw.Build!, "build");
    AssertEq(2, btw.Previews.Count, "previews");
    Assert(btw.Document is not null, "document decompressed");
    Assert(btw.ClassNames().Contains("TextData"), "TextData class found");
    Assert(btw.Utf16Strings().Any(s => s.Contains("999999")), "rollover landmark");
});

Check("BtwFile flags old format", () =>
{
    var btw = BtwFile.Parse(new byte[] { 0x16, (byte)'B', (byte)'a', (byte)'r' });
    Assert(btw.IsOldFormat, "IsOldFormat");
});

Check("BtwGeometry extracts dims/anchors/text record", () =>
{
    byte[] doc = BuildGeometryDoc();
    var dims = BtwGeometry.LabelDims(doc);
    Assert(dims is { W: 6500, H: 4000 }, $"dims: {dims}");

    var anchors = BtwGeometry.Anchors(doc);
    Assert(anchors.Any(a => a.Class == "TextData" && a.X == 100 && a.Y == 200),
           "form-1 TextData anchor");
    Assert(anchors.Any(a => a.Class == "BcC128Data" && a.X == 300 && a.Y == 400),
           "form-1 barcode anchor");
    Assert(anchors.Any(a => a.Class == "box" && a.X == 1184 && a.Y == 765),
           "form-2 box anchor");

    var texts = BtwGeometry.TextObjects(doc);
    AssertEq(1, texts.Count, "one text record");
    var t = texts[0];
    AssertEq(1184, t.X, "x"); AssertEq(765, t.Y, "y");
    AssertEq(4, t.RefPoint, "refpoint C");
    AssertEq(2, t.Align!.Value, "align right");
    AssertEq(1965, t.WidthMils!.Value, "box width");
    AssertEq(240, t.FontHeightMils, "cell height");
    AssertEq(90.0, t.RotationDeg, "rotation");
    AssertEq(700, t.Weight, "weight");
    AssertEq("PART NO (P)", t.Value!, "value string");

    var lines = BtwGeometry.LineObjects(doc);
    Assert(lines.Any(l => l is { X1: 180, Y1: 1022, X2: 6180, Y2: 1022, ThicknessMils: 10 }),
           "line record");

    var bars = BtwGeometry.BarcodeObjects(doc);
    Assert(bars.Any(b => b.Symbology == "code128"), "symbology mapped");

    var (xtl, ytl) = BtwGeometry.TopLeft(1184, 765, 4, 1964, 640);
    AssertEq(202.0, xtl, "TL recovery x"); AssertEq(445.0, ytl, "TL recovery y");
});

Check("BtwConverter emits validating 0.2 SVG", () =>
{
    byte[] doc = BuildGeometryDoc();
    byte[] container = WrapInContainer(doc);
    var btw = BtwFile.Parse(container);
    Assert(btw.Document is not null, "container parsed");
    var result = BtwConverter.Convert(btw, "synthetic.btw");
    Assert(result.Stats.DimsFound, "dims found");
    AssertEq(6500, result.Stats.LabelW, "label W");
    AssertEq(1, result.Stats.Texts, "texts");
    Assert(result.Svg.Contains("data-width=\"1965\""), "width carried");
    Assert(result.Svg.Contains("data-layer=\"Fields\""), "layered");
    Assert(result.Svg.Contains("PART NO (P)"), "value carried");
    var errs = TemplateValidator.Validate(EtiqTemplate.Parse(result.Svg))
        .Where(f => f.Severity == Severity.Error).ToList();
    Assert(errs.Count == 0, "converted SVG validates clean: " + string.Join("; ", errs));
});
#endif

// ---------- PngImage / PdfWriter ----------

Check("PngImage decodes + thumbnails", () =>
{
    var png = PngImage.Decode(MinimalPng(64, 40, 200, 220, 255));
    AssertEq(64, png.Width, "width");
    AssertEq((byte)200, png.Rgb[0], "r");
    var thumb = png.Thumbnail(32);
    AssertEq(32, thumb.Width, "thumb width");
    AssertEq(20, thumb.Height, "thumb height");
});

Check("PdfWriter emits parseable PDF", () =>
{
    var pdf = new PdfWriter(100, 100);
    pdf.NewPage();
    pdf.DrawImage(PngImage.Decode(MinimalPng(4, 4, 10, 20, 30)), 10, 10, 40, 40);
    pdf.DrawText("hello (test)", 10, 60, 11);
    string tmp = Path.GetTempFileName();
    pdf.Save(tmp);
    var bytes = File.ReadAllBytes(tmp);
    Assert(bytes.Length > 200, "non-trivial size");
    Assert(Encoding.Latin1.GetString(bytes, 0, 8) == "%PDF-1.4", "header");
    Assert(Encoding.Latin1.GetString(bytes).Contains("%%EOF"), "trailer");
    File.Delete(tmp);
});

// ---------- EpicorClient against a local mock ----------

Check("EpicorClient BAQ filter mode", () =>
{
    var handler = new MockHandler(req =>
    {
        AssertEq("GET", req.Method.Method, "method");
        AssertEq("/SaaS509/api/v2/odata/KHC/BaqSvc/DB-JobLabel/Data", req.RequestUri!.AbsolutePath, "path");
        Assert(req.RequestUri.Query.Contains("$filter=") || req.RequestUri.Query.Contains("%24filter="), "has $filter");
        Assert(Uri.UnescapeDataString(req.RequestUri.Query).Contains("JobAsmbl_JobNum eq '12345'"), "filter expr");
        Assert(req.Headers.TryGetValues("x-api-key", out var k) && k.First() == "KEY", "api key header");
        Assert(req.Headers.Authorization?.Scheme == "Basic", "basic auth");
        return Json("""{"value":[{"JobAsmbl_PartNum":"P-100","Part_PartsPerContainer":24}]}""");
    });
    using var c = new EpicorClient(Cfg(), handler);
    var row = c.FetchBaqRowAsync("12345").GetAwaiter().GetResult();
    AssertEq("P-100", row["JobAsmbl_PartNum"].GetString()!, "part");
    AssertEq(24, row["Part_PartsPerContainer"].GetInt32(), "pcs");
});

Check("EpicorClient param mode + quote escaping in filter", () =>
{
    var cfg = Cfg(); cfg.QueryMode = "param";
    var handler = new MockHandler(req =>
    {
        Assert(req.RequestUri!.Query.Contains("JobNum=J-1"), "param query");
        return Json("""{"value":[{"A":1}]}""");
    });
    using var c = new EpicorClient(cfg, handler);
    c.FetchBaqRowAsync("J-1").GetAwaiter().GetResult();

    var handler2 = new MockHandler(req =>
    {
        Assert(Uri.UnescapeDataString(req.RequestUri!.Query).Contains("eq 'O''Brien'"), "quote doubled");
        return Json("""{"value":[{"A":1}]}""");
    });
    using var c2 = new EpicorClient(Cfg(), handler2);
    c2.FetchBaqRowAsync("O'Brien").GetAwaiter().GetResult();
});

Check("EpicorClient guards + errors", () =>
{
    using var c = new EpicorClient(Cfg(), new MockHandler(_ => Json("""{"value":[]}""")));
    try { c.FetchBaqRowAsync(" ").GetAwaiter().GetResult(); throw new Exception("empty job accepted"); }
    catch (ArgumentException) { }
    try { c.FetchBaqRowAsync("NOPE").GetAwaiter().GetResult(); throw new Exception("empty value accepted"); }
    catch (EpicorException ex) { Assert(ex.Message.Contains("Invalid Job Number"), "message"); }

    using var c2 = new EpicorClient(Cfg(), new MockHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("denied") }));
    try { c2.FetchBaqRowAsync("1").GetAwaiter().GetResult(); throw new Exception("401 accepted"); }
    catch (EpicorException ex) { Assert(ex.Message.Contains("HTTP 401"), "401 surfaced"); }
});

Check("EpicorClient function call", () =>
{
    var handler = new MockHandler(req =>
    {
        AssertEq("POST", req.Method.Method, "method");
        AssertEq("/SaaS509/api/v2/efx/KHC/EtiqCounters/NextSerial", req.RequestUri!.AbsolutePath, "efx path");
        string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert(body.Contains("\"counter\":\"DUDEK\""), "payload");
        return Json("""{"next":100042}""");
    });
    using var c = new EpicorClient(Cfg(), handler);
    var result = c.CallFunctionAsync("EtiqCounters", "NextSerial", new { counter = "DUDEK" })
        .GetAwaiter().GetResult();
    AssertEq(100042, result.GetProperty("next").GetInt32(), "counter value");
});

// ---------- CredentialStore (platform-dependent bits only where possible) ----------

Check("CredentialStore secret detection + passthrough", () =>
{
    Assert(!CredentialStore.SecretPresent("PASTE-API-KEY-HERE"), "placeholder not secret");
    Assert(!CredentialStore.SecretPresent("EPICOR-PASSWORD"), "placeholder not secret");
    Assert(!CredentialStore.SecretPresent("dpapi:abc"), "encrypted not re-encrypted");
    Assert(!CredentialStore.SecretPresent(""), "empty not secret");
    Assert(CredentialStore.SecretPresent("real-key-123"), "real secret detected");
    AssertEq("plain", CredentialStore.Unprotect("plain"), "non-prefixed passthrough");
    if (OperatingSystem.IsWindows())
        AssertEq("s3cret", CredentialStore.Unprotect(CredentialStore.Protect("s3cret")), "roundtrip");
    else
        try { CredentialStore.Protect("x"); throw new Exception("should throw off-Windows"); }
        catch (PlatformNotSupportedException) { }
});

// ---------- counters ----------

Check("SerialFormat padding + base36", () =>
{
    AssertEq("000042", SerialFormat.Format(42, "000000"), "zero pad");
    AssertEq("42", SerialFormat.Format(42), "no format");
    AssertEq("11", SerialFormat.Format(37, null, "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"), "base36 37");
    AssertEq("00ZZ", SerialFormat.Format(36 * 36 - 1, "0000", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"), "base36 pad");
});

Check("EpicorCounterProvider reserve + peek via mock", () =>
{
    var handler = new MockHandler(req =>
    {
        string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        if (req.RequestUri!.AbsolutePath.EndsWith("/EtiqCounters/NextSerial"))
        {
            Assert(body.Contains("\"counter\":\"DUDEK\"") && body.Contains("\"count\":500"), "payload: " + body);
            return Json("""{"next":100042}""");
        }
        Assert(req.RequestUri.AbsolutePath.EndsWith("/EtiqCounters/PeekSerial"), "path");
        return Json("""{"current":100542}""");
    });
    using var c = new EpicorClient(Cfg(), handler);
    var p = new EpicorCounterProvider(c);
    AssertEq(100042L, p.ReserveAsync("DUDEK", 500).GetAwaiter().GetResult(), "reserve");
    AssertEq(100542L, p.PeekAsync("DUDEK").GetAwaiter().GetResult(), "peek");
    try { p.ReserveAsync(" ").GetAwaiter().GetResult(); throw new Exception("empty counter accepted"); }
    catch (ArgumentException) { }
});

Check("LocalFileCounterProvider blocks + persistence", () =>
{
    string tmp = Path.GetTempFileName();
    File.Delete(tmp);
    var p = new LocalFileCounterProvider(tmp);
    AssertEq(1L, p.ReserveAsync("A").GetAwaiter().GetResult(), "first");
    AssertEq(2L, p.ReserveAsync("A", 10).GetAwaiter().GetResult(), "block start");
    AssertEq(12L, p.PeekAsync("A").GetAwaiter().GetResult(), "peek after block");
    AssertEq(1L, p.ReserveAsync("B").GetAwaiter().GetResult(), "independent counter");
    var p2 = new LocalFileCounterProvider(tmp); // fresh instance, same file
    AssertEq(12L, p2.ReserveAsync("A").GetAwaiter().GetResult(), "persisted");
    File.Delete(tmp);
});

// ---------- ZPL raster ----------

Check("ZplRaster mono conversion", () =>
{
    // 16x2: left half black, right half white
    int w = 16, h = 2;
    var rgb = new byte[w * h * 3];
    for (int y = 0; y < h; y++)
        for (int x = 8; x < 16; x++)
            for (int k = 0; k < 3; k++)
                rgb[(y * w + x) * 3 + k] = 255;
    var mono = ZplRaster.ToMono(rgb, w, h);
    AssertEq(4, mono.Length, "stride*rows");
    AssertEq((byte)0xFF, mono[0], "black byte");
    AssertEq((byte)0x00, mono[1], "white byte");
});

Check("ZplRaster compression round-trips", () =>
{
    // Synthetic label-ish raster: white field, black bars, repeated rows.
    int stride = 12, rows = 40;
    var mono = new byte[stride * rows];
    for (int y = 5; y < 12; y++)                  // solid black band
        for (int i = 0; i < stride; i++) mono[y * stride + i] = 0xFF;
    for (int y = 20; y < 36; y++)                 // "barcode" pattern, identical rows
        for (int i = 2; i < 10; i += 2) mono[y * stride + i] = 0xB6;
    mono[0] = 0x80; mono[stride * rows - 1] = 0x01; // corners

    string job = ZplRaster.BuildJob(mono, stride * 8, rows);
    Assert(job.StartsWith("^XA") && job.EndsWith("^XZ"), "wrapper");
    Assert(job.Contains($"^GFA,{mono.Length},{mono.Length},{stride},"), "GFA header");

    string data = job[(job.IndexOf($",{stride},") + $",{stride},".Length)..job.IndexOf("^FS")];
    var decoded = ZplRaster.DecodeGfData(data, stride, rows);
    Assert(decoded.SequenceEqual(mono), "compressed round-trip");
    Assert(data.Length < mono.Length * 2 / 4, $"actually compresses ({data.Length} vs {mono.Length * 2} plain hex)");

    string plainJob = ZplRaster.BuildJob(mono, stride * 8, rows, compress: false);
    string plainData = plainJob[(plainJob.IndexOf($",{stride},") + $",{stride},".Length)..plainJob.IndexOf("^FS")];
    Assert(ZplRaster.DecodeGfData(plainData, stride, rows).SequenceEqual(mono), "plain hex round-trip");
});

Check("QR / DataMatrix / PDF417 encoders (decode-verified vectors)", () =>
{
    // hashes were locked in after every symbol below decoded correctly in
    // BOTH zbar and zxing-cpp (see --dump-barcodes): any change to the
    // encoders that alters output must be re-verified externally
    static string MatrixHash(bool[,]? m)
    {
        if (m is null) return "(null)";
        int h = m.GetLength(0), w = m.GetLength(1);
        var bytes = new List<byte> { (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w };
        byte cur = 0;
        int nb = 0;
        foreach (bool b in m)
        {
            cur = (byte)(cur << 1 | (b ? 1 : 0));
            if (++nb == 8) { bytes.Add(cur); cur = 0; nb = 0; }
        }
        if (nb > 0) bytes.Add((byte)(cur << (8 - nb)));
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes.ToArray()))[..16];
    }

    var qr = QrCode.Encode("HELLO WORLD", 'M');
    Assert(qr is not null && qr.GetLength(0) == 21, "HELLO WORLD fits v1 (21x21)");
    AssertEq("E0E90D18D5F0D93A", MatrixHash(qr), "qr M vector");
    AssertEq("A4EDE1AC0FF10109", MatrixHash(QrCode.Encode("8675309", 'H')), "qr numeric H vector");
    AssertEq("25720C57F14FA93C", MatrixHash(QrCode.Encode(new string('x', 180), 'Q')),
        "qr v7+ vector (alignment on timing + version info)");
    AssertEq("2B8CBD540D41E35D", MatrixHash(DataMatrix.Encode("ETIQ-0042")), "datamatrix vector");
    AssertEq("AE437A8FC5C9C861", MatrixHash(Pdf417.Encode("Etiquette PDF417", 4)), "pdf417 vector");
    Assert(QrCode.Encode(new string('x', 3000), 'H') is null, "qr over capacity -> null");
    Assert(DataMatrix.Encode(new string('x', 1600)) is null, "datamatrix over capacity -> null");
    Assert(Pdf417.Encode(new string('x', 900), 6) is null, "pdf417 over 90 rows -> null");

    // barcode attribute validation: ecc / columns / logo
    var bc = EtiqTemplate.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" width="6in" height="4in" viewBox="0 0 6000 4000">
          <metadata><etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
            <etiq:field name="F" source="prompt" caption="f"/>
          </etiq:label></metadata>
          <g data-layer="L">
            <rect x="1" y="1" width="900" height="900" data-barcode="qr"
                  data-field="F" data-ecc="Z"/>
            <rect x="1" y="1" width="900" height="900" data-barcode="code128"
                  data-field="F" data-columns="99" data-logo="etiq"/>
            <rect x="1" y="1" width="900" height="900" data-barcode="qr"
                  data-field="F" data-logo="etiq" data-ecc="L" data-logo-scale="200"/>
          </g>
        </svg>
        """);
    var bf = TemplateValidator.Validate(bc);
    Assert(bf.Any(f => f.Code == "barcode-ecc" && f.Severity == Severity.Error), "bad data-ecc errs");
    Assert(bf.Any(f => f.Code == "barcode-columns" && f.Severity == Severity.Error), "bad data-columns errs");
    Assert(bf.Any(f => f.Code == "barcode-logo" && f.Message.Contains("only applies to qr")),
        "logo on non-qr warns");
    Assert(bf.Any(f => f.Code == "barcode-logo" && f.Message.Contains("forces ECC")),
        "logo + non-H ecc warns");
    Assert(bf.Any(f => f.Code == "barcode-logo" && f.Severity == Severity.Error
                    && f.Message.Contains("data-logo-scale")),
        "out-of-range logo scale errs");
});

Check("editor: layer ops — move-to-layer (group units), reorder, remove", () =>
{
    var doc = Etiq.Editor.Core.EditorDoc.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 6000 4000">
          <g data-layer="A">
            <text id="t1" x="1" y="2" font-size="10">one</text>
            <g><text id="t2" x="1" y="2" font-size="10">two</text>
               <text id="t3" x="1" y="2" font-size="10">three</text></g>
          </g>
          <g data-layer="B">
            <text id="t4" x="1" y="2" font-size="10">four</text>
          </g>
        </svg>
        """);
    var layers = doc.Layers;
    AssertEq(2, layers.Count, "two layers");
    var a = layers[0];
    var b = layers[1];

    // moving a GROUP MEMBER moves the whole group (one layer per group)
    var t2 = doc.Objects.First(o => (string?)o.El.Attribute("id") == "t2");
    doc.MoveToLayer(new[] { t2 }, b);
    Assert(t2.El.Parent!.Parent == b.El, "whole group moved into B");
    var t3 = doc.Objects.First(o => (string?)o.El.Attribute("id") == "t3");
    AssertEq("B", t3.Layer?.Name, "sibling member came along");
    doc.Undo.Undo();
    AssertEq("A", doc.Objects.First(o => (string?)o.El.Attribute("id") == "t3").Layer?.Name,
        "undo restores the original layer");

    // layer z reorder: raise A above B, then edge is a no-op
    Assert(doc.MoveLayer(a, +1), "raise succeeds");
    AssertEq("B", doc.Layers[0].Name, "B now paints first (bottom)");
    Assert(!doc.MoveLayer(a, +1), "already on top -> no-op");
    doc.Undo.Undo();
    AssertEq("A", doc.Layers[0].Name, "undo restores layer order");

    // remove layer takes its contents; undo brings both back
    int before = doc.Objects.Count;
    doc.RemoveLayer(b);
    AssertEq(1, doc.Layers.Count, "layer removed");
    Assert(doc.Objects.Count < before, "its objects went with it");
    doc.Undo.Undo();
    AssertEq(2, doc.Layers.Count, "undo restores the layer");
    AssertEq(before, doc.Objects.Count, "and its contents");
});

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed;

// ---------- helpers ----------

static EpicorConfig Cfg() => new()
{
    BaseUrl = "https://instance.epicorsaas.com/SaaS509/",
    Company = "KHC",
    ApiKey = "KEY",
    Username = "user",
    Password = "pw",
    BaqId = "DB-JobLabel",
    QueryMode = "filter",
    JobParam = "JobNum",
    FieldMap = new() { ["JobNum"] = "JobAsmbl_JobNum" },
};

static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json"),
};

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "HANDOFF.md")))
        d = d.Parent;
    return d?.FullName ?? ".";
}

#if ETIQ_BTW
/// Synthetic MFC archive exercising every decoded geometry finding
/// (docs/btw-format.md): form-1 class anchors, Box-tag record with
/// LOGFONT + align/width blob + reference point, dims record, line record.
static byte[] BuildGeometryDoc()
{
    var d = new List<byte>();
    void I32(params int[] vals) { foreach (var v in vals) d.AddRange(BitConverter.GetBytes(v)); }
    void Mfc(string s)
    {
        d.AddRange(new byte[] { 0xFF, 0xFE, 0xFF, (byte)s.Length });
        d.AddRange(Encoding.Unicode.GetBytes(s));
    }

    // label dims record: plausible mils pair repeating within 100 bytes
    I32(-1, 65535, -65536, 256, 6500, 4000);
    d.AddRange(new byte[20]);
    I32(6500, 4000);

    // form-1 class registrations with anchors: FF FF 01 00 <len:2> Name <x> <y>
    void Reg(string cls, int x, int y)
    {
        d.AddRange(new byte[] { 0xFF, 0xFF, 0x01, 0x00 });
        d.AddRange(BitConverter.GetBytes((short)cls.Length));
        d.AddRange(Encoding.ASCII.GetBytes(cls));
        I32(x, y);
    }
    Reg("TextData", 100, 200);
    Reg("BcC128Data", 300, 400);

    // LineData 7-int geometry
    I32(6000, 180, 1022, 6180, 1022, 500, 10);

    d.AddRange(new byte[32]);

    // --- text record: value + LOGFONT + align blob + Box sub-object ---
    Mfc("PART NO (P)");                       // value string
    d.AddRange(new byte[8]);
    I32(-240, 0, 900, 0, 700);                // LOGFONT: height,width,esc,orient,weight
    d.AddRange(new byte[8]);
    d.AddRange(new byte[] { 0x00, 0x00 });    // align blob: 00 00 FF*8 a align 0 width
    d.AddRange(Enumerable.Repeat((byte)0xFF, 8));
    I32(1, 2, 0, 1965);
    d.AddRange(new byte[8]);
    Mfc("Box 1");                             // sub-object name
    d.AddRange(Enumerable.Repeat((byte)0x11, 16));   // GUID
    d.AddRange(new byte[] { 0xFF, 0xFF, 0x01, 0x80 }); // runtime-class index tag
    I32(1184, 765);                           // anchor (center refpoint location)
    d.AddRange(new byte[12]);
    d.AddRange(new byte[] { 0xFF, 0xFE, 0xFF, 0x00, 0x00, 0x01, 0x04 }); // refpoint C
    d.AddRange(new byte[16]);
    return d.ToArray();
}

/// Wrap an archive in a structurally valid 10.1 container.
static byte[] WrapInContainer(byte[] doc)
{
    var hdr = Encoding.Latin1.GetBytes(
        "Bar Tender Format File\r\nApplication: Version=10.1 SR3; build=2954; Edition=X\r\n" +
        "Printer: Name=TestPrinter; Model=M\r\n");
    byte[] png = MinimalPng(8, 8, 1, 2, 3);
    using var ms = new MemoryStream();
    using (var zs = new System.IO.Compression.ZLibStream(ms,
               System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        zs.Write(doc);
    byte[] z = ms.ToArray();
    var outp = new List<byte>();
    outp.AddRange(hdr); outp.Add(0x1A);
    outp.AddRange(BitConverter.GetBytes(88634)); outp.AddRange(BitConverter.GetBytes(88634));
    outp.AddRange(BitConverter.GetBytes(png.Length)); outp.AddRange(png);
    outp.AddRange(BitConverter.GetBytes(7));
    outp.AddRange(BitConverter.GetBytes(png.Length)); outp.AddRange(png);
    outp.AddRange(new byte[] { 0x00, 0x01 }); outp.AddRange(z);
    return outp.ToArray();
}

static byte[] BuildSyntheticBtw()
{
    var hdr = Encoding.Latin1.GetBytes(
        "Bar Tender Format File\r\nApplication: Version=10.1 SR3; build=2954; Edition=X\r\n" +
        "Printer: Name=TestPrinter; Model=M\r\n");
    byte[] png = MinimalPng(8, 8, 1, 2, 3);
    var doc = new List<byte>();
    doc.AddRange(Encoding.ASCII.GetBytes("TextData\0"));
    doc.AddRange(Encoding.Unicode.GetBytes("Serial No. (3S)")); doc.AddRange(new byte[2]);
    doc.AddRange(MfcStr("1")); doc.AddRange(MfcStr("999999"));
    byte[] z = Deflate(doc.ToArray());
    var outp = new List<byte>();
    outp.AddRange(hdr); outp.Add(0x1A);
    outp.AddRange(BitConverter.GetBytes(88634)); outp.AddRange(BitConverter.GetBytes(88634));
    outp.AddRange(BitConverter.GetBytes(png.Length)); outp.AddRange(png);
    outp.AddRange(BitConverter.GetBytes(7));
    outp.AddRange(BitConverter.GetBytes(png.Length)); outp.AddRange(png);
    outp.AddRange(new byte[] { 0x00, 0x01 }); outp.AddRange(z);
    return outp.ToArray();

    static byte[] MfcStr(string s)
    {
        var b = new List<byte> { 0xFF, 0xFE, 0xFF, (byte)s.Length };
        b.AddRange(Encoding.Unicode.GetBytes(s));
        return b.ToArray();
    }
    static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(ms,
                   System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(data);
        return ms.ToArray();
    }
}
#endif

static byte[] MinimalPng(int w, int h, byte r, byte g, byte b)
{
    using var ms = new MemoryStream();
    void Chunk(string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        ms.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes);
        ms.Write(data);
        var crc = Crc32(typeBytes.Concat(data).ToArray());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        ms.Write(len);
    }
    ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    var ihdr = new byte[13];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
    ihdr[8] = 8; ihdr[9] = 2;
    Chunk("IHDR", ihdr);
    var raw = new List<byte>();
    for (int y = 0; y < h; y++)
    {
        raw.Add(0);
        for (int x = 0; x < w; x++) { raw.Add(r); raw.Add(g); raw.Add(b); }
    }
    using var zms = new MemoryStream();
    using (var zs = new System.IO.Compression.ZLibStream(zms,
               System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        zs.Write(raw.ToArray());
    Chunk("IDAT", zms.ToArray());
    Chunk("IEND", Array.Empty<byte>());
    return ms.ToArray();

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var by in data)
        {
            crc ^= by;
            for (int k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
        return ~crc;
    }
}

sealed class MockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public MockHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_respond(request));
}

/// <summary>Debug tool behind --dump-barcodes: renders a deterministic
/// spread of QR / DataMatrix / PDF417 symbols to PNGs + manifest.tsv so
/// external decoders (zbar, zxing) can verify the encoders end-to-end.</summary>
static class BarcodeDump
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var manifest = new List<string>();
        var rnd = new Random(42);
        const string pool = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,-/:#()";
        string RandStr(int len, string set)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < len; i++) sb.Append(set[rnd.Next(set.Length)]);
            return sb.ToString();
        }
        var samples = new List<string>
        {
            "A", "42", "HELLO WORLD", "https://etiquette.dev/t?id=12345",
            "1234567890123456789012345678901234567890",
        };
        foreach (int len in new[] { 3, 8, 17, 33, 60, 100, 180, 300, 500, 900, 1600 })
        {
            samples.Add(RandStr(len, pool));
            samples.Add(RandStr(len, "0123456789"));
            samples.Add(RandStr(len, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-./:"));
        }
        int n = 0;
        void Save(string kind, string content, bool[,]? m, int yScale)
        {
            if (m is null) { manifest.Add($"{kind}\tNULL\t{Escape(content)}"); return; }
            string file = $"{kind}_{n++:000}.png";
            File.WriteAllBytes(Path.Combine(dir, file), MatrixPng(m, 4, 4 * yScale, 8));
            manifest.Add($"{kind}\t{file}\t{Escape(content)}");
        }
        foreach (var s in samples)
        {
            foreach (char ecc in "LMQH") Save($"qr{ecc}", s, Etiq.Core.QrCode.Encode(s, ecc), 1);
            // the logo configuration: ECC H + version floor 2
            Save("qrG", s, Etiq.Core.QrCode.Encode(s, 'H', minVersion: 2), 1);
            Save("dm", s, Etiq.Core.DataMatrix.Encode(s), 1);
            Save("pdf417", s, Etiq.Core.Pdf417.Encode(s), 3);
        }
        // pdf417 column spread
        foreach (int cols in new[] { 2, 4, 10, 16 })
            Save($"pdf417c{cols}", samples[8], Etiq.Core.Pdf417.Encode(samples[8], cols), 3);
        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), manifest);
        Console.WriteLine($"dumped {n} PNGs to {dir}");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");

    /// <summary>Gray-8 PNG of a module matrix (dark = black) with quiet zone.</summary>
    public static byte[] MatrixPng(bool[,] m, int xScale, int yScale, int quiet)
    {
        int mh = m.GetLength(0), mw = m.GetLength(1);
        // quiet zone is measured in modules at the horizontal scale
        int w = mw * xScale + 2 * quiet * xScale;
        int h = mh * yScale + 2 * quiet * xScale;
        var raw = new byte[h * (w + 1)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (w + 1);
            raw[o] = 0; // filter none
            for (int x = 0; x < w; x++)
            {
                int myy = (y - quiet * xScale) / yScale, mxx = (x - quiet * xScale) / xScale;
                bool dark = y >= quiet * xScale && x >= quiet * xScale
                    && myy < mh && mxx < mw && m[myy, mxx];
                raw[o + 1 + x] = dark ? (byte)0 : (byte)255;
            }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            ms.Write(len);
            var tb = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(tb);
            ms.Write(data);
            var crcBuf = new byte[tb.Length + data.Length];
            tb.CopyTo(crcBuf, 0);
            data.CopyTo(crcBuf, tb.Length);
            Span<byte> crc = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcBuf));
            ms.Write(crc);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // grayscale
        Chunk("IHDR", ihdr);
        using var zms = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(zms,
                   System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(raw);
        Chunk("IDAT", zms.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();

        static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var by in data)
            {
                crc ^= by;
                for (int k = 0; k < 8; k++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
            }
            return ~crc;
        }
    }
}
