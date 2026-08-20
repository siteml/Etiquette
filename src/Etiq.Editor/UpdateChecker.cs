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

    public static Version Current =>
        typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);

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

    /// <summary>Update flavor preference, stored in
    /// %APPDATA%\Etiquette\settings.json: "auto" (default — match the
    /// running build, but a standalone install with the runtime present is
    /// OFFERED the lighter build) | "standalone" | "framework".</summary>
    public static string UpdateFlavor
    {
        get
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                    if (doc.RootElement.TryGetProperty("updateFlavor", out var f))
                    {
                        string? v = f.GetString();
                        if (v is "standalone" or "framework") return v;
                    }
                }
            }
            catch { /* default */ }
            return "auto";
        }
        set
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                    new { updateFlavor = value == "auto" ? null : value }));
            }
            catch { /* best-effort */ }
        }
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
            ? new Release(v, tag, url, saUrl, saName, fdUrl, fdName)
            : null;
    }
}
