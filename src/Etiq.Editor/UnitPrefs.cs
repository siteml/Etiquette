using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>The editor's current display unit and dot density.
///
/// Two layers (docs/grid-guides.md): the APP DEFAULT lives in settings.json
/// ("units"; Help → Options) and applies to templates that say nothing;
/// a template's <c>etiq:view units=</c> overrides it once written (View →
/// Units with a document open writes the template, not the setting).
/// Coordinates in the document stay mils throughout — this only changes
/// what the status bar, inspector, rulers and dialogs show and accept.</summary>
internal static class UnitPrefs
{
    private const string Key = "units";
    private static DisplayUnit? _dflt;

    /// <summary>The open document's view, or null. Wired by MainForm.</summary>
    public static Func<ViewSettings?>? DocView { get; set; }

    /// <summary>App default (settings.json). Options dialog writes this.</summary>
    public static DisplayUnit Default
    {
        get => _dflt ??= Units.Parse(UpdateChecker.GetSetting(Key));
        set
        {
            if (value == Default) return;
            _dflt = value;
            UpdateChecker.SetSetting(Key, Units.Name(value));
            Changed?.Invoke();
        }
    }

    /// <summary>Effective unit: template override, else app default. A
    /// `dots` choice with no density known falls back to mils in display.</summary>
    public static DisplayUnit Current => DocView?.Invoke()?.Units ?? Default;

    /// <summary>Raised when the effective unit or density may have changed
    /// (setting, template override, document switched); listeners re-render.</summary>
    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>Head density for the `dots` unit, from the document's
    /// etiq:view. 0 = unknown → dots display as mils.</summary>
    public static double DotsPerMm => DocView?.Invoke()?.DotsPerMm ?? 0;

    public static string Suffix => Units.Suffix(Current);
    public static string F(double mils) => Units.Format(mils, Current, DotsPerMm);
    public static string FS(double mils) => Units.FormatWithSuffix(mils, Current, DotsPerMm);
    public static bool TryParse(string? text, out double mils) =>
        Units.TryParseLength(text, Current, out mils, DotsPerMm);
    public static double NudgeMils => Units.NudgeMils(Current, DotsPerMm);

    // ---- on-screen grid density (Help → Options) ----

    private const string GridPxKey = "gridMinPx";
    private static int? _gridPx;

    /// <summary>Minimum on-screen spacing (px) between drawn grid lines. The
    /// canvas thins the grid (every 2nd/4th/8th… pitch) until lines are at
    /// least this far apart; snapping always uses the true pitch. Fine = 6,
    /// normal = 12, coarse = 24. A personal/monitor preference, so a
    /// setting — not template state.</summary>
    public static int GridMinPx
    {
        get => _gridPx ??= int.TryParse(UpdateChecker.GetSetting(GridPxKey), out int n) && n >= 3 && n <= 200 ? n : 12;
        set
        {
            int v = Math.Clamp(value, 3, 200);
            if (v == GridMinPx) return;
            _gridPx = v;
            UpdateChecker.SetSetting(GridPxKey, v.ToString());
            Changed?.Invoke();
        }
    }

    private const string GridMajorKey = "gridMajor";
    private static int? _gridMajor;

    /// <summary>Every Nth grid line is drawn heavier so cells can be
    /// counted. 0 = automatic: 10, or 8 on a dot grid (= 1 mm on an
    /// 8 dots/mm head). Program setting, not template state.</summary>
    public static int GridMajor
    {
        get => _gridMajor ??= int.TryParse(UpdateChecker.GetSetting(GridMajorKey), out int n) && n >= 0 && n <= 100 ? n : 0;
        set
        {
            int v = Math.Clamp(value, 0, 100);
            if (v == GridMajor) return;
            _gridMajor = v;
            UpdateChecker.SetSetting(GridMajorKey, v.ToString());
            Changed?.Invoke();
        }
    }

    /// <summary>Resolve the 0 = auto case for a given grid.</summary>
    public static int GridMajorFor(bool dotGrid) => GridMajor > 1 ? GridMajor : (dotGrid ? 8 : 10);

    private const string GridMajorModeKey = "gridMajorMode";
    private static bool? _majorAdaptive;

    /// <summary>false = FIXED: a heavy line every N grid cells, whatever the
    /// zoom (N × pitch is a physical distance; zooming out only thins the
    /// minors beneath it). true = ADAPTIVE: a heavy line every N DRAWN
    /// lines — as zoom-out thins the minors the majors thin with them, so
    /// there are always N visible cells per heavy square.</summary>
    public static bool GridMajorAdaptive
    {
        get => _majorAdaptive ??= UpdateChecker.GetSetting(GridMajorModeKey) == "adaptive";
        set
        {
            if (value == GridMajorAdaptive) return;
            _majorAdaptive = value;
            UpdateChecker.SetSetting(GridMajorModeKey, value ? "adaptive" : "fixed");
            Changed?.Invoke();
        }
    }

    // ---- rulers / guides visibility (View menu; per operator) ----

    private static bool? _rulers, _showGuides;

    /// <summary>Draw the rulers along the canvas top/left (Design mode).</summary>
    public static bool ShowRulers
    {
        get => _rulers ??= UpdateChecker.GetSetting("rulers") != "off";
        set { if (value == ShowRulers) return; _rulers = value; UpdateChecker.SetSetting("rulers", value ? null : "off"); Changed?.Invoke(); }
    }

    /// <summary>Draw (and snap to) the template's guides. Hidden guides
    /// never snap — snapping to something invisible is a trap.</summary>
    public static bool ShowGuides
    {
        get => _showGuides ??= UpdateChecker.GetSetting("guides") != "off";
        set { if (value == ShowGuides) return; _showGuides = value; UpdateChecker.SetSetting("guides", value ? null : "off"); Changed?.Invoke(); }
    }

    // ---- screen redaction (View → Redact Sensitive; per operator) ----

    private static bool? _redact;

    /// <summary>Show stand-ins for data-sensitive elements everywhere the
    /// editor echoes content. Never affects printing. Off by default so
    /// day-to-day work sees real values; flip on before a remote demo.</summary>
    public static bool Redact
    {
        get => _redact ??= UpdateChecker.GetSetting("redact") == "on";
        set { if (value == Redact) return; _redact = value; UpdateChecker.SetSetting("redact", value ? "on" : null); Changed?.Invoke(); }
    }

    // ---- proof prints on sheet printers (Help → Options) ----

    private static string? _sheetOrient;

    /// <summary>Orientation for proofs on office (sheet) printers:
    /// "label" (default: a wide label prints landscape, like on stock),
    /// "portrait" or "landscape" — hard-set when a driver won't cooperate.</summary>
    public static string SheetOrientation
    {
        get
        {
            if (_sheetOrient is null)
            {
                string? v = UpdateChecker.GetSetting("sheetOrientation");
                _sheetOrient = v is "portrait" or "landscape" ? v : "label";
            }
            return _sheetOrient;
        }
        set
        {
            string v = value is "portrait" or "landscape" ? value : "label";
            if (v == SheetOrientation) return;
            _sheetOrient = v;
            UpdateChecker.SetSetting("sheetOrientation", v == "label" ? null : v);
        }
    }

    public static readonly (int Px, string Caption)[] GridDensityChoices =
    {
        (6, "fine (6 px)"), (12, "normal (12 px)"), (24, "coarse (24 px)"), (48, "sparse (48 px)"),
    };

    /// <summary>Menu/options order and captions. `dots` is offered only
    /// when the document knows its density.</summary>
    public static (DisplayUnit Unit, string Caption)[] Choices(bool includeDots) =>
        includeDots
            ? new[] { (DisplayUnit.In, "Inches (in)"), (DisplayUnit.Mm, "Millimetres (mm)"),
                      (DisplayUnit.Mils, "Mils (1/1000 in)"), (DisplayUnit.Dots, "Printer dots") }
            : new[] { (DisplayUnit.In, "Inches (in)"), (DisplayUnit.Mm, "Millimetres (mm)"),
                      (DisplayUnit.Mils, "Mils (1/1000 in)") };
}
