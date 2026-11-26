using System.Text;
using System.Text.Json;

namespace Modular.Core.Installers.Fallout4;

/// <summary>One line of <c>plugins.txt</c>.</summary>
/// <param name="Name">The plugin's file name.</param>
/// <param name="Active">Whether the game loads it (the line starts with <c>*</c>).</param>
public sealed record Fallout4PluginEntry(string Name, bool Active);

/// <summary>What <see cref="Fallout4PluginList.Update"/> changed.</summary>
public sealed record Fallout4PluginListResult(
    IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Warnings);

/// <summary>
/// Fallout 4's load order: <c>plugins.txt</c> in <c>%LOCALAPPDATA%\Fallout4</c>, one plugin
/// per line, first loaded first, <c>*</c> marking the active ones. The base game, its DLC and
/// the Creation Club files named in <c>Fallout4.ccc</c> load before it without being listed.
/// </summary>
/// <remarks>
/// The game, the Creations menu and other tools write the same file, so Modular edits it in
/// place: it adds the plugins it deploys, removes the ones it added once their mod is gone
/// (recorded in <see cref="OwnershipFileName"/>), and leaves every other line where it is.
/// </remarks>
public static class Fallout4PluginList
{
    public const string FileName = "plugins.txt";

    /// <summary>The folder under <c>%LOCALAPPDATA%</c> holding <see cref="FileName"/>.</summary>
    public const string Folder = "Fallout4";

    /// <summary>Plugins Modular added to the list, so only those are ever taken out.</summary>
    public const string OwnershipFileName = "plugins.modular.json";

    /// <summary>The most full (non-light) plugins the engine can load, base game included.</summary>
    public const int MaxFullPlugins = 254;

    /// <summary>The base game and DLC masters, in the order the engine loads them.</summary>
    public static readonly IReadOnlyList<string> BaseMasters =
    [
        "Fallout4.esm",
        "DLCRobot.esm",
        "DLCworkshop01.esm",
        "DLCCoast.esm",
        "DLCworkshop02.esm",
        "DLCworkshop03.esm",
        "DLCNukaWorld.esm",
        "DLCUltraHighResolution.esm",
    ];

    private const string Header =
        "# This file is used by Fallout4 to keep track of your downloaded content.\r\n" +
        "# Please do not modify this file.\r\n";

    /// <summary>
    /// The plugins the engine loads before <c>plugins.txt</c>, in order: the base game and DLC
    /// masters present, then the Creation Club plugins <c>Fallout4.ccc</c> names that are present.
    /// </summary>
    public static IReadOnlyList<string> ImplicitPlugins(string gameDirectory)
    {
        var data = DataDirectory(gameDirectory);
        var present = Directory.Exists(data)
            ? Directory.EnumerateFiles(data).Select(Path.GetFileName).OfType<string>()
                .ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var list = new List<string>();
        foreach (var master in BaseMasters)
        {
            if (present.TryGetValue(master, out var actual))
                list.Add(actual);
        }

        var ccc = Path.Combine(gameDirectory, "Fallout4.ccc");
        if (File.Exists(ccc))
        {
            foreach (var line in File.ReadAllLines(ccc))
            {
                var name = line.Trim();
                if (name.Length > 0 && present.TryGetValue(name, out var actual) &&
                    !list.Contains(actual, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(actual);
                }
            }
        }
        return list;
    }

    /// <summary>The game's <c>Data</c> folder, whatever its case on disk.</summary>
    public static string DataDirectory(string gameDirectory)
    {
        if (Directory.Exists(gameDirectory))
        {
            var found = Directory.EnumerateDirectories(gameDirectory)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Data", StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                return found;
        }
        return Path.Combine(gameDirectory, "Data");
    }

    /// <summary>Reads <c>plugins.txt</c>; empty when it doesn't exist yet (the game hasn't run).</summary>
    public static List<Fallout4PluginEntry> Read(string path)
    {
        var entries = new List<Fallout4PluginEntry>();
        if (!File.Exists(path))
            return entries;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var active = line.StartsWith('*');
            var name = active ? line[1..].Trim() : line;
            if (name.Length > 0 && !entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                entries.Add(new Fallout4PluginEntry(name, active));
        }
        return entries;
    }

    /// <summary>Whether Modular has ever added plugins to this list.</summary>
    public static bool HasOwnershipRecord(string pluginsPath) => File.Exists(OwnershipPath(pluginsPath));

    /// <summary>
    /// Brings <c>plugins.txt</c> in line with what Modular deployed. New plugins are added
    /// active, masters after the last master already listed and others at the end, then each
    /// is moved after any master it needs that is listed later. Plugins Modular added whose
    /// mod is gone (not in <paramref name="managed"/>, and no longer in <c>Data</c>) are taken
    /// out. Everything else keeps its place and its on/off state.
    /// </summary>
    /// <param name="pluginsPath">The <c>plugins.txt</c> path.</param>
    /// <param name="gameDirectory">The game folder, to read the plugins in <c>Data</c>.</param>
    /// <param name="managed">Modular's deployed plugins, in Modular's load order.</param>
    public static Fallout4PluginListResult Update(string pluginsPath, string gameDirectory, IReadOnlyList<string> managed)
    {
        var data = DataDirectory(gameDirectory);
        var entries = Read(pluginsPath);
        var ownershipPath = OwnershipPath(pluginsPath);
        var owned = ReadOwnership(ownershipPath);
        var implicitPlugins = ImplicitPlugins(gameDirectory);
        var managedSet = new HashSet<string>(managed, StringComparer.OrdinalIgnoreCase);
        var headers = new Dictionary<string, Fallout4Plugin?>(StringComparer.OrdinalIgnoreCase);
        Fallout4Plugin? HeaderOf(string name)
        {
            if (!headers.TryGetValue(name, out var header))
                headers[name] = header = Fallout4Plugin.Read(Path.Combine(data, name));
            return header;
        }

        var removed = new List<string>();
        entries.RemoveAll(e =>
        {
            var gone = owned.Contains(e.Name) && !managedSet.Contains(e.Name) && !File.Exists(Path.Combine(data, e.Name));
            if (gone)
                removed.Add(e.Name);
            return gone;
        });
        owned.RemoveWhere(n => removed.Contains(n, StringComparer.OrdinalIgnoreCase));

        var added = new List<string>();
        foreach (var name in managed)
        {
            if (implicitPlugins.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var entry = new Fallout4PluginEntry(name, Active: true);
            if (HeaderOf(name)?.IsMaster == true)
            {
                var lastMaster = entries.FindLastIndex(e => HeaderOf(e.Name)?.IsMaster == true);
                entries.Insert(lastMaster + 1, entry);
            }
            else
            {
                entries.Add(entry);
            }
            added.Add(name);
            owned.Add(name);
        }

        // A new plugin goes after every master it names that the list holds below it.
        foreach (var name in added)
        {
            var index = entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            var masters = HeaderOf(name)?.Masters ?? [];
            var lastMaster = masters
                .Select(m => entries.FindIndex(e => string.Equals(e.Name, m, StringComparison.OrdinalIgnoreCase)))
                .DefaultIfEmpty(-1).Max();
            if (lastMaster > index)
            {
                var entry = entries[index];
                entries.RemoveAt(index);
                entries.Insert(lastMaster, entry);
            }
        }

        if (added.Count > 0 || removed.Count > 0)
        {
            Write(pluginsPath, entries);
            WriteOwnership(ownershipPath, owned);
        }

        var warnings = new List<string>();
        var full = implicitPlugins.Count(p => HeaderOf(p)?.IsLight != true) +
                   entries.Count(e => e.Active && HeaderOf(e.Name)?.IsLight != true);
        if (full > MaxFullPlugins)
        {
            warnings.Add($"{full} full plugins are active, more than the {MaxFullPlugins} the game can load. " +
                         "Turn some off or use light (ESL) versions, or the game won't start.");
        }
        return new Fallout4PluginListResult(added, removed, warnings);
    }

    /// <summary>
    /// Rewrites the order of <c>plugins.txt</c> to <paramref name="names"/>, keeping each line's
    /// on/off state.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The names aren't a reordering of the list, a plugin would load before a master it needs,
    /// or a master would follow a plugin that isn't one (the engine loads masters first anyway).
    /// </exception>
    public static void SetOrder(string pluginsPath, string gameDirectory, IReadOnlyList<string> names)
    {
        var data = DataDirectory(gameDirectory);
        var entries = Read(pluginsPath);
        var byName = entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        if (names.Count != entries.Count || names.Any(n => !byName.ContainsKey(n)) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            throw new InvalidOperationException(
                "The new order has to list the same plugins as plugins.txt; adding and removing plugins is done by installing and uninstalling mods.");
        }

        var position = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        var seenNonMaster = (string?)null;
        foreach (var name in names)
        {
            var header = Fallout4Plugin.Read(Path.Combine(data, name));
            if (header is null)
                continue;
            if (header.IsMaster && seenNonMaster is not null)
            {
                throw new InvalidOperationException(
                    $"{name} is a master file, and the game always loads masters before other plugins, so it can't follow {seenNonMaster}.");
            }
            if (!header.IsMaster)
                seenNonMaster ??= name;
            foreach (var master in header.Masters)
            {
                if (position.TryGetValue(master, out var at) && at > position[name])
                    throw new InvalidOperationException($"{name} needs {master}, so {master} has to load first.");
            }
        }

        Write(pluginsPath, names.Select(n => byName[n]).ToList());
    }

    private static void Write(string path, IReadOnlyList<Fallout4PluginEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder(Header);
        foreach (var e in entries)
            text.Append(e.Active ? "*" : string.Empty).Append(e.Name).Append("\r\n");
        var temporary = path + ".modular.tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string OwnershipPath(string pluginsPath) =>
        Path.Combine(Path.GetDirectoryName(pluginsPath) ?? string.Empty, OwnershipFileName);

    private static HashSet<string> ReadOwnership(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) is { } names)
                return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Unreadable: treat as never written, so nothing is removed that Modular can't prove it added.
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteOwnership(string path, HashSet<string> owned)
    {
        var temporary = path + ".modular.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(owned.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()));
        File.Move(temporary, path, overwrite: true);
    }
}
