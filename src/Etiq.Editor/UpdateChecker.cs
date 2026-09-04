using System.Net.Http;
using System.Text.Json;

namespace Etiq.Editor;

/// <summary>
/// GitHub-releases update check. The repository ships WITH the build (the
/// Repo constant below — set it once the GitHub project exists; users
/// never configure anything). While it is empty, the startup check does
/// nothing and Help → Check for Updates just says the source isn't set.
/// A newer release prompts to open its download page. Version source =
/// the assembly version (&lt;Version&gt; in Etiq.Editor.csproj — bump per
/// release; tags compare as vX.Y.Z).
/// </summary>
public static class UpdateChecker
{
    /// <summary>"owner/name" of the GitHub project. Empty = update checks
    /// are disabled (the repo isn't published yet).</summary>
    public const string Repo = "siteml/Etiquette";

    public static bool Configured => Repo != "";

    /// <summary>Url = the release page; the asset pairs are the direct
    /// downloads for each publish flavor (null when the release lacks
    /// one — fall back to the release page). The CALLER picks which to
    /// offer (build flavor, runtime availability, user preference).</summary>
    public sealed record Release(Version Version, string Tag, string Url,
                                 string? StandaloneUrl, string? StandaloneName,
                                 string? FrameworkUrl, string? FrameworkName);

    /// <summary>Running version, normalized to THREE components. The
    /// assembly reports 0.8.0.0 while a release tag parses to 0.8.0, and
    /// System.Version treats the missing revision as -1 — so un-normalized,
    /// "same version" compares FALSE and the same-version flavor-switch
    /// offer never fires.</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Which publish flavor is THIS running build? A self-contained
    /// single-file publish bundles the runtime, so core assemblies have no
    /// on-disk location (Assembly.Location == ""); a framework-dependent
    /// build loads System.Private.CoreLib from the machine's shared
    /// dotnet install. Updates should fetch the SAME flavor — that also
    /// answers "is the runtime installed": a framework-dependent build
    /// wouldn't be running without it.</summary>
    public static bool IsSelfContained =>
        string.IsNullOrEmpty(typeof(object).Assembly.Location);

    /// <summary>Is the .NET 8 Desktop Runtime installed on this machine?
    /// (Checked so a standalone install can be offered the much lighter
    /// framework-dependent build.) Looks for an 8.x directory under the
    /// shared Microsoft.WindowsDesktop.App folder of the machine's dotnet
    /// install; any failure reads as "not installed".</summary>
    public static bool DesktopRuntimeInstalled()
    {
        try
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                         System.IO.Path.Combine(Environment.GetFolderPath(
                             Environment.SpecialFolder.ProgramFiles), "dotnet"),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string shared = System.IO.Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(shared) &&
                    Directory.EnumerateDirectories(shared, "8.*").Any())
                    return true;
            }
        }
        catch { /* treat as not installed */ }
        return false;
    }

    // ---------- settings store ----------
    // %APPDATA%\Etiquette\settings.json — read-merge-write, so each setting
    // can be saved independently without clobbering the others.

    private static Dictionary<string, string> LoadSettings()
    {
        var d = new Dictionary<string, string>();
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                        d[p.Name] = p.Value.GetString() ?? "";
                    else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        d[p.Name] = p.Value.GetBoolean() ? "true" : "false";
                    // null / other kinds: treat as absent
                }
            }
        }
        catch { /* fresh settings */ }
        return d;
    }

    private static void SaveSetting(string key, string? value)
    {
        try
        {
            var d = LoadSettings();
            if (value is null) d.Remove(key);
            else d[key] = value;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(d));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Generic access to settings.json for other app settings
    /// (e.g. the print-station template path). Null = absent/remove.</summary>
    public static string? GetSetting(string key) =>
        LoadSettings().TryGetValue(key, out var v) && v != "" ? v : null;

    public static void SetSetting(string key, string? value) => SaveSetting(key, value);

    /// <summary>Update flavor preference: "auto" (default — match the
    /// running build, but a standalone install with the runtime present is
    /// OFFERED the lighter build) | "standalone" | "framework".</summary>
    public static string UpdateFlavor
    {
        get => LoadSettings().TryGetValue("updateFlavor", out var v) &&
               v is "standalone" or "framework" ? v : "auto";
        set => SaveSetting("updateFlavor", value == "auto" ? null : value);
    }

    /// <summary>A release version the user chose to skip ("0.6.0"-style,
    /// null = none). The silent startup check stays quiet about exactly
    /// this version; a NEWER release clears the skip implicitly, and an
    /// explicit Help → Check for Updates always shows what it finds.</summary>
    public static string? SkipVersion
    {
        get => LoadSettings().TryGetValue("skipVersion", out var v) && v != "" ? v : null;
        set => SaveSetting("skipVersion", value);
    }

    /// <summary>Automatic update check at startup (default on). Off =
    /// updates are only ever found via Help → Check for Updates.</summary>
    public static bool AutoCheck
    {
        get => !(LoadSettings().TryGetValue("autoUpdateCheck", out var v) && v == "false");
        set => SaveSetting("autoUpdateCheck", value ? null : "false");
    }

    /// <summary>CHANGELOG.md from the repo's default branch — shown in the
    /// update dialog. Null on any failure (the dialog copes).</summary>
    public static async Task<string?> FetchChangelogAsync(string repo = Repo)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("etiqedit-update-check");
            return await http.GetStringAsync(
                $"https://raw.githubusercontent.com/{repo}/main/CHANGELOG.md");
        }
        catch { return null; }
    }

    private static string SettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Etiquette", "settings.json");

    /// <summary>Latest release of the configured repo with both flavor
    /// assets, or null when it has no releases or the tag isn't a
    /// parseable version. Throws on network / HTTP errors — the caller
    /// decides whether that's worth mentioning.</summary>
    public static async Task<Release?> FetchLatestAsync(string repo = Repo)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("etiqedit-update-check");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string json = await http.GetStringAsync(
            $"https://api.github.com/repos/{repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string url = doc.RootElement.TryGetProperty("html_url", out var u)
            ? u.GetString() ?? "" : "";

        string? saUrl = null, saName = null, fdUrl = null, fdName = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n)
                    ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                string? dl = a.TryGetProperty("browser_download_url", out var du)
                    ? du.GetString() : null;
                if (name.Contains("standalone", StringComparison.OrdinalIgnoreCase))
                { saName ??= name; saUrl ??= dl; }
                else
                { fdName ??= name; fdUrl ??= dl; }
            }
        }

        string ver = tag.TrimStart('v', 'V');
        return Version.TryParse(ver.Contains('.') ? ver : ver + ".0", out var v)
            // normalize to three components like Current — Version's missing
            // parts are -1 and poison equality/ordering otherwise
            ? new Release(new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0)),
                          tag, url, saUrl, saName, fdUrl, fdName)
            : null;
    }
}
