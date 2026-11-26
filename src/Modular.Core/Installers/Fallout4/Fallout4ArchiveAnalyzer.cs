using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Fallout4;

/// <summary>
/// Works out where each file of a Fallout 4 mod archive goes.
/// </summary>
/// <remarks>
/// <para>
/// Almost everything belongs in <c>Data/</c>. Archives rarely start there: most hold a
/// <c>Data/</c> folder, or the plugins and data folders (<c>meshes/</c>, <c>F4SE/</c>...)
/// at the top, often inside one wrapper folder named after the mod. The data root is the
/// shallowest folder that holds a plugin or <c>.ba2</c>, or one of those data folders.
/// </para>
/// <para>
/// A few things go beside <c>Fallout4.exe</c> instead: the Script Extender (F4SE), ENB and
/// ReShade files, and a <c>Root/</c> folder (the convention mods use for such files). F4SE's
/// loader is also installed as <c>Fallout4Launcher.exe</c>, the program Steam starts, so
/// playing from Steam (and Game Mode) starts the game through F4SE.
/// </para>
/// </remarks>
public static class Fallout4ArchiveAnalyzer
{
    public const string F4seLoader = "f4se_loader.exe";

    /// <summary>The program Steam starts for Fallout 4.</summary>
    public const string SteamLauncher = "Fallout4Launcher.exe";

    /// <summary>Folders that only exist inside <c>Data/</c>.</summary>
    public static readonly IReadOnlySet<string> DataFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "materials", "scripts", "sound", "music", "interface", "strings",
        "video", "f4se", "mcm", "shadersfx", "lodsettings", "vis", "seq", "terrain", "distantlod",
        "lod", "programs", "aaf",
    };

    /// <summary>ENB and ReShade files, which go beside the executable.</summary>
    public static readonly IReadOnlySet<string> GameRootFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "d3d11.dll", "d3d10.dll", "dxgi.dll", "d3dcompiler_46e.dll", "enbseries.ini", "enblocal.ini",
        "enbhost.exe", "reshade.ini", "reshadepreset.ini",
    };

    /// <summary>Folders of ENB and ReShade files, which go beside the executable.</summary>
    public static readonly IReadOnlySet<string> GameRootFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "enbseries", "reshade-shaders",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".html", ".htm", ".pdf", ".rtf", ".doc", ".docx", ".url", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",
    };

    private static readonly HashSet<string> DocumentFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs", "documentation", "readme", "screenshots", "images", "fomod", "src", "source",
    };

    public static Fallout4InstallLayout Analyze(IReadOnlyList<ArchiveEntry> entries, string? archiveName = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var files = entries
            .Where(e => !e.IsDirectory)
            .Select(e => (Entry: e, Path: Normalize(e.FullName)))
            .Where(f => f.Path.Length > 0 && !IsJunk(f.Path))
            .ToList();

        if (files.Count == 0)
            return Fallout4InstallLayout.Unknown("Archive is empty");

        var loader = files.FirstOrDefault(f => string.Equals(FileName(f.Path), F4seLoader, StringComparison.OrdinalIgnoreCase));
        if (loader.Entry is not null)
            return AnalyzeScriptExtender(files, DirectoryOf(loader.Path));

        var layout = new Fallout4InstallLayout();
        var dataRoots = FindDataRoots(files.Select(f => f.Path).ToList());

        if (dataRoots.Count > 1)
        {
            return Fallout4InstallLayout.Refused(
                $"The archive holds several versions side by side ({string.Join(", ", dataRoots.Select(r => r.Length == 0 ? "(top)" : r))}). " +
                "Pick one: install the file for the version you want from the mod's page, or extract that folder and install it on its own.");
        }

        var dataRoot = dataRoots.Count == 1 ? dataRoots[0] : null;
        foreach (var (entry, path) in files)
        {
            if (RootRoute(path) is { } rootDestination)
            {
                layout.FileRoutes[entry.FullName] = FileRoute.Heuristic(rootDestination, PackageTypes.Fo4GameRoot);
                layout.GameRootFiles.Add(rootDestination);
                continue;
            }

            if (dataRoot is null || !IsUnder(path, dataRoot))
            {
                layout.SkippedFiles.Add(path);
                continue;
            }

            var relative = dataRoot.Length == 0 ? path : path[(dataRoot.Length + 1)..];
            if (IsDocument(relative))
            {
                layout.SkippedFiles.Add(path);
                continue;
            }

            layout.FileRoutes[entry.FullName] = FileRoute.Heuristic("Data/" + relative, PackageTypeOf(relative));
            if (!relative.Contains('/') && Fallout4Plugin.IsPluginFile(relative))
                layout.Plugins.Add(relative);
        }

        if (layout.FileRoutes.Count == 0)
        {
            return Fallout4InstallLayout.Refused(
                "Modular can't tell where these files go in Fallout 4: there is no Data folder, plugin, " +
                ".ba2 or data folder (meshes, textures, scripts, F4SE...) in the archive.");
        }

        layout.Confidence = layout.Plugins.Count > 0 || dataRoot is not null ? 0.9 : 0.8;
        layout.Reason = Describe(layout, dataRoot);
        return layout;
    }

    /// <summary>
    /// F4SE: its loader and DLL go beside the executable and its <c>Data/</c> into the game's,
    /// as its readme says; <c>src/</c> and the text files stay in the archive. The loader is
    /// also installed as <see cref="SteamLauncher"/>.
    /// </summary>
    private static Fallout4InstallLayout AnalyzeScriptExtender(List<(ArchiveEntry Entry, string Path)> files, string loaderDir)
    {
        var layout = new Fallout4InstallLayout { IsScriptExtender = true, Confidence = 0.95 };
        var prefix = loaderDir.Length == 0 ? string.Empty : loaderDir + "/";
        foreach (var (entry, path) in files)
        {
            var relative = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
            if (relative is null)
            {
                layout.SkippedFiles.Add(path);
                continue;
            }

            var name = FileName(relative);
            if (!relative.Contains('/') && name.StartsWith("f4se_", StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                layout.FileRoutes[entry.FullName] = FileRoute.Declared(name, PackageTypes.Fo4ScriptExtender);
                layout.GameRootFiles.Add(name);
                if (string.Equals(name, F4seLoader, StringComparison.OrdinalIgnoreCase))
                    layout.ExtraRoutes.Add((entry.FullName, FileRoute.Declared(SteamLauncher, PackageTypes.Fo4ScriptExtender)));
            }
            else if (relative.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            {
                layout.FileRoutes[entry.FullName] = FileRoute.Declared("Data/" + relative[5..], PackageTypes.Fo4LooseData);
            }
            else
            {
                layout.SkippedFiles.Add(path);
            }
        }

        layout.Reason = "Fallout 4 Script Extender (F4SE): its loader beside Fallout4.exe, also as Fallout4Launcher.exe " +
                        "so Steam starts the game through F4SE";
        return layout;
    }

    /// <summary>
    /// The shallowest folders that are a data root: an explicit <c>Data/</c> folder, or one
    /// holding a plugin or <c>.ba2</c>, or a data folder (<c>meshes/</c>, <c>F4SE/</c>...).
    /// A root inside another root doesn't count.
    /// </summary>
    private static List<string> FindDataRoots(List<string> paths)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var segments = path.Split('/');
            // An explicit Data folder (not a data folder's own "data" subfolder).
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (DataFolders.Contains(segments[i]) || IsGameRootFolder(segments[i]))
                    break;
                if (string.Equals(segments[i], "Data", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(string.Join('/', segments.Take(i + 1)));
                    goto next;
                }
            }

            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (IsGameRootFolder(segments[i]))
                    goto next;
                if (DataFolders.Contains(segments[i]))
                {
                    roots.Add(string.Join('/', segments.Take(i)));
                    goto next;
                }
            }

            var name = segments[^1];
            if (Fallout4Plugin.IsPluginFile(name) || name.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
                roots.Add(string.Join('/', segments.Take(segments.Length - 1)));
            next:;
        }

        return roots
            .Where(r => !roots.Any(o => !string.Equals(o, r, StringComparison.OrdinalIgnoreCase) && IsUnder(r, o)))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Where a file beside the executable goes, or null when it isn't one: a <c>Root/</c>
    /// folder's contents, an ENB or ReShade file or folder. Only at the top of the archive or
    /// inside one wrapper folder; anything under <c>Data/</c> or a data folder stays data.
    /// </summary>
    private static string? RootRoute(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < Math.Min(2, segments.Length); i++)
        {
            var segment = segments[i];
            if (i == segments.Length - 1)
                return GameRootFiles.Contains(segment) ? segment : null;
            if (string.Equals(segment, "Root", StringComparison.OrdinalIgnoreCase))
                return string.Join('/', segments.Skip(i + 1));
            if (GameRootFolders.Contains(segment))
                return string.Join('/', segments.Skip(i));
            if (string.Equals(segment, "Data", StringComparison.OrdinalIgnoreCase) || DataFolders.Contains(segment))
                return null;
        }
        return null;
    }

    private static bool IsGameRootFolder(string segment) =>
        GameRootFolders.Contains(segment) || string.Equals(segment, "Root", StringComparison.OrdinalIgnoreCase);

    private static string PackageTypeOf(string relative)
    {
        var name = FileName(relative);
        if (!relative.Contains('/') && Fallout4Plugin.IsPluginFile(name))
            return PackageTypes.Fo4Plugin;
        if (!relative.Contains('/') && name.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
            return PackageTypes.Fo4Archive;
        if (relative.StartsWith("F4SE/Plugins/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return PackageTypes.Fo4F4sePlugin;
        return PackageTypes.Fo4LooseData;
    }

    /// <summary>Readmes and pictures at the top of the data root, and documentation folders.</summary>
    private static bool IsDocument(string relative)
    {
        var segments = relative.Split('/');
        if (segments.Length > 1)
            return DocumentFolders.Contains(segments[0]);
        return DocumentExtensions.Contains(Path.GetExtension(relative));
    }

    private static string Describe(Fallout4InstallLayout layout, string? dataRoot)
    {
        var parts = new List<string>();
        if (layout.Plugins.Count > 0)
            parts.Add($"{layout.Plugins.Count} plugin(s): {string.Join(", ", layout.Plugins)}");
        var dataFiles = layout.FileRoutes.Values.Count(r => r.Destination.StartsWith("Data/", StringComparison.Ordinal));
        if (dataFiles > 0)
            parts.Add($"{dataFiles} file(s) into Data/{(dataRoot is { Length: > 0 } ? $" (from {dataRoot}/)" : "")}");
        if (layout.GameRootFiles.Count > 0)
            parts.Add($"{layout.GameRootFiles.Count} file(s) beside Fallout4.exe");
        return "Fallout 4 mod: " + string.Join("; ", parts);
    }

    private static bool IsUnder(string path, string root) =>
        root.Length == 0 || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsJunk(string path)
    {
        var name = FileName(path);
        return path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}

/// <summary>Where a Fallout 4 archive's files go; see <see cref="Fallout4ArchiveAnalyzer"/>.</summary>
public sealed class Fallout4InstallLayout
{
    /// <summary>Whether the archive is a Fallout 4 mod Modular can place.</summary>
    public bool CanHandle => Refusal is null && FileRoutes.Count > 0;

    /// <summary>
    /// Why the archive can't be installed although it is a Fallout 4 archive. The installer
    /// still claims it, so the install stops with this reason instead of another installer
    /// putting the files in the wrong place.
    /// </summary>
    public string? Refusal { get; private init; }

    public double Confidence { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>The archive is F4SE itself.</summary>
    public bool IsScriptExtender { get; init; }

    /// <summary>Plugins it puts at the top of <c>Data/</c>.</summary>
    public List<string> Plugins { get; } = new();

    /// <summary>Files it puts beside <c>Fallout4.exe</c>.</summary>
    public List<string> GameRootFiles { get; } = new();

    public List<string> SkippedFiles { get; } = new();

    /// <summary>Archive entry name to where it goes, relative to the game folder.</summary>
    public Dictionary<string, FileRoute> FileRoutes { get; } = new(StringComparer.Ordinal);

    /// <summary>Second copies of an entry (F4SE's loader as the Steam launcher).</summary>
    public List<(string Entry, FileRoute Route)> ExtraRoutes { get; } = new();

    public static Fallout4InstallLayout Unknown(string reason) => new() { Reason = reason };

    public static Fallout4InstallLayout Refused(string reason) => new() { Reason = reason, Refusal = reason, Confidence = 0.5 };
}
