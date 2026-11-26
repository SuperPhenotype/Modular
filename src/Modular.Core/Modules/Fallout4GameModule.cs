using System.Diagnostics;
using Modular.Core.Archives;
using Modular.Core.Installers.Fallout4;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Compatibility;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Modules;

/// <summary>
/// Game module for Fallout 4 (Steam AppID 377160).
/// </summary>
/// <remarks>
/// <para>
/// Mods go into <c>Data/</c> (<see cref="Fallout4ModInstaller"/>, and FOMOD scripts through
/// <see cref="Fallout4FomodInstaller"/>). The Script Extender, ENB and ReShade go beside
/// <c>Fallout4.exe</c>.
/// </para>
/// <para>
/// The load order is <c>plugins.txt</c> in the game's <c>AppData/Local/Fallout4</c>, which the
/// game and other tools also write: Modular adds its plugins there and keeps masters first
/// (<see cref="Fallout4PluginList"/>). After each deploy, <see cref="Fallout4Ini"/> makes sure
/// the game loads loose files and reads that list.
/// </para>
/// </remarks>
public sealed class Fallout4GameModule : GameModuleBase
{
    /// <summary>F4SE on Nexus Mods, for messages that send the user to install it.</summary>
    public const string F4seNexusId = "42147";

    private static readonly string[] GameProcessNames = ["Fallout4", "Fallout4.exe", "f4se_loader", "f4se_loader.exe"];

    /// <summary>The official DLC each base master belongs to, for messages.</summary>
    private static readonly Dictionary<string, string> DlcNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DLCRobot.esm"] = "Automatron",
        ["DLCworkshop01.esm"] = "Wasteland Workshop",
        ["DLCCoast.esm"] = "Far Harbor",
        ["DLCworkshop02.esm"] = "Contraptions Workshop",
        ["DLCworkshop03.esm"] = "Vault-Tec Workshop",
        ["DLCNukaWorld.esm"] = "Nuka-World",
        ["DLCUltraHighResolution.esm"] = "High Resolution Texture Pack",
    };

    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly Func<bool> _isGameRunning;

    public Fallout4GameModule(IArchiveReaderFactory? archiveReaderFactory = null, Func<bool>? isGameRunning = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _isGameRunning = isGameRunning ?? DetectRunningGame;
    }

    public override string ModuleId => "fallout4";

    public override string DisplayName => "Fallout 4";

    public override IReadOnlyList<string> GameIds { get; } = [Sdk.GameIds.Fallout4, "377160"];

    public override IReadOnlyList<int> SteamAppIds { get; } = [377160];

    protected override IReadOnlyList<string> PrimaryAnchors { get; } = ["Fallout4.exe", "Data/Fallout4.esm"];

    protected override IReadOnlyList<string> SupportingAnchors { get; } = ["Fallout4Launcher.exe", "Fallout4.ccc"];

    /// <summary>Fallout 4 VR shares the layout; its plugins list and INIs are its own.</summary>
    protected override IReadOnlyList<string> RejectingAnchors { get; } = ["Fallout4VR.exe"];

    public override IReadOnlyList<IModInstaller> GetInstallers() =>
        [new Fallout4FomodInstaller(_archiveReaderFactory), new Fallout4ModInstaller(_archiveReaderFactory)];

    /// <summary>The game's <c>plugins.txt</c>; null when its AppData folder can't be located.</summary>
    public static string? ResolvePluginsPath(GameInstallation installation)
    {
        var localAppData = ModRoots.ResolveStandardRoot(installation, ModRoots.LocalAppData);
        return string.IsNullOrEmpty(localAppData)
            ? null
            : Path.Combine(localAppData, Fallout4PluginList.Folder, Fallout4PluginList.FileName);
    }

    /// <summary>The game's <c>My Games/Fallout4</c> folder (INIs, saves); null when it can't be located.</summary>
    public static string? ResolveMyGamesPath(GameInstallation installation)
    {
        var documents = ModRoots.ResolveStandardRoot(installation, ModRoots.Documents);
        return string.IsNullOrEmpty(documents)
            ? null
            : Path.Combine(documents, Fallout4Ini.Folder.Replace('/', Path.DirectorySeparatorChar));
    }

    // ── Load order ──────────────────────────────────────────────────────────

    public override LoadOrderKind LoadOrderKind => LoadOrderKind.External;

    /// <summary>
    /// Adds the plugins Modular deployed to <c>plugins.txt</c> and takes out the ones whose
    /// mod is gone. See <see cref="Fallout4PluginList.Update"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The game is running, the list can't be located, or it is written but has a warning.
    /// The deployer reports the message as a warning.
    /// </exception>
    public override Task WriteLoadOrderAsync(GameInstallation installation, IReadOnlyList<DeployedMod> mods, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);

        var pluginsPath = ResolvePluginsPath(installation)
            ?? throw new InvalidOperationException("the game's AppData folder (where plugins.txt lives) could not be located");

        var managed = mods.Where(m => m.Enabled)
            .OrderBy(m => m.LoadPosition)
            .SelectMany(m => PluginsOf(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (managed.Count == 0 && !Fallout4PluginList.HasOwnershipRecord(pluginsPath))
            return Task.CompletedTask;

        if (_isGameRunning())
        {
            throw new InvalidOperationException(
                "Fallout 4 is running and keeps plugins.txt itself, so the plugin list was not updated. Close the game and deploy again.");
        }

        var result = Fallout4PluginList.Update(pluginsPath, installation.InstallPath, managed);
        if (result.Warnings.Count > 0)
            throw new InvalidOperationException("plugins.txt is up to date, but " + string.Join(" ", result.Warnings));
        return Task.CompletedTask;
    }

    public override EngineOrderDescription? DescribeEngineOrder(GameInstallation installation, IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (ResolvePluginsPath(installation) is not { } pluginsPath)
            return null;
        var order = Fallout4PluginList.ImplicitPlugins(installation.InstallPath)
            .Concat(Fallout4PluginList.Read(pluginsPath).Where(e => e.Active).Select(e => e.Name))
            .ToList();
        return new EngineOrderDescription(
            "the base game, its DLC and Creation Club files first, then plugins.txt from top to bottom; a later plugin " +
            "wins, and masters (.esm, .esl) always load before other plugins. Modular adds its plugins after the masters " +
            "they need and leaves every other line where it is",
            order,
            pluginsPath);
    }

    /// <summary>
    /// The base game, DLC and Creation Club plugins (locked first, as the engine loads them),
    /// then every line of <c>plugins.txt</c>, saying which are Modular's.
    /// </summary>
    public override EngineOrder? ReadEngineOrder(GameInstallation installation, IReadOnlyList<DeployedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(mods);
        if (ResolvePluginsPath(installation) is not { } pluginsPath)
            return null;

        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var plugin in PluginsOf(mod))
                owners.TryAdd(plugin, mod.ModId);
        }

        var entries = Fallout4PluginList.ImplicitPlugins(installation.InstallPath)
            .Select(p => new EngineOrderEntry(p, p, null, Locked: true))
            .Concat(Fallout4PluginList.Read(pluginsPath).Select(e =>
                new EngineOrderEntry(e.Name, e.Active ? e.Name : $"{e.Name} (off)", owners.GetValueOrDefault(e.Name))))
            .ToList();
        return new EngineOrder(entries, pluginsPath);
    }

    /// <summary>Rewrites the order of <c>plugins.txt</c>; the base game's plugins stay first.</summary>
    /// <exception cref="InvalidOperationException">The game is running, or the order is refused (see <see cref="Fallout4PluginList.SetOrder"/>).</exception>
    public override Task SetEngineOrderAsync(GameInstallation installation, IReadOnlyList<string> entryIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(entryIds);

        var pluginsPath = ResolvePluginsPath(installation)
            ?? throw new InvalidOperationException("the game's AppData folder (where plugins.txt lives) could not be located");
        if (_isGameRunning())
            throw new InvalidOperationException("Fallout 4 is running and keeps plugins.txt itself, so the order was not changed. Close the game and try again.");

        var implicitPlugins = Fallout4PluginList.ImplicitPlugins(installation.InstallPath);
        if (!entryIds.Take(implicitPlugins.Count).SequenceEqual(implicitPlugins, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The base game, DLC and Creation Club plugins always load first, in their own order.");

        Fallout4PluginList.SetOrder(pluginsPath, installation.InstallPath, entryIds.Skip(implicitPlugins.Count).ToList());
        return Task.CompletedTask;
    }

    // ── Checks ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Masters a loaded plugin needs that aren't loaded (the game stops at the main menu or
    /// crashes without them), and F4SE plugins with no F4SE to load them.
    /// </summary>
    public override IReadOnlyList<UnmetDependency> FindUnmetDependencies(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var unmet = new List<UnmetDependency>();
        var data = Fallout4PluginList.DataDirectory(installation.InstallPath);
        if (!Directory.Exists(data))
            return unmet;

        var present = Directory.EnumerateFiles(data).Select(Path.GetFileName).OfType<string>()
            .Where(Fallout4Plugin.IsPluginFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = new HashSet<string>(Fallout4PluginList.ImplicitPlugins(installation.InstallPath), StringComparer.OrdinalIgnoreCase);
        if (ResolvePluginsPath(installation) is { } pluginsPath)
        {
            foreach (var entry in Fallout4PluginList.Read(pluginsPath).Where(e => e.Active && present.Contains(e.Name)))
                loaded.Add(entry.Name);
        }

        foreach (var plugin in loaded.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var header = Fallout4Plugin.Read(Path.Combine(data, plugin));
            foreach (var master in header?.Masters ?? [])
            {
                if (loaded.Contains(master))
                    continue;
                var name = present.Contains(master) ? $"{master} (installed but turned off in plugins.txt)"
                    : DlcNames.TryGetValue(master, out var dlc) ? $"{master} (the {dlc} DLC, from Steam)"
                    : master;
                unmet.Add(new UnmetDependency(plugin, "Data/" + plugin, name, master));
            }
        }

        var f4sePlugins = Path.Combine(data, "F4SE", "Plugins");
        var hasF4se = File.Exists(Path.Combine(installation.InstallPath, Fallout4ArchiveAnalyzer.F4seLoader));
        if (!hasF4se && Directory.Exists(f4sePlugins))
        {
            foreach (var dll in Directory.EnumerateFiles(f4sePlugins, "*.dll").Select(Path.GetFileName).OfType<string>().Order())
            {
                unmet.Add(new UnmetDependency(dll, "Data/F4SE/Plugins/" + dll,
                    $"Fallout 4 Script Extender (F4SE), Nexus Mods {F4seNexusId}", "f4se"));
            }
        }
        return unmet;
    }

    /// <summary>The plugins and F4SE in the game folder, by file name.</summary>
    public override IReadOnlyList<string> ListPresentModNames(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var names = new List<string>();
        var data = Fallout4PluginList.DataDirectory(installation.InstallPath);
        if (Directory.Exists(data))
            names.AddRange(Directory.EnumerateFiles(data).Select(Path.GetFileName).OfType<string>().Where(Fallout4Plugin.IsPluginFile));
        if (File.Exists(Path.Combine(installation.InstallPath, Fallout4ArchiveAnalyzer.F4seLoader)))
            names.Add("Fallout 4 Script Extender (F4SE)");
        return names;
    }

    /// <summary>Warns when a mod replaces one of the base game's or DLC's own masters.</summary>
    public override IReadOnlyList<ModDiagnostic> ValidateStaged(GameInstallation installation, StagedMod stagedMod)
    {
        ArgumentNullException.ThrowIfNull(stagedMod);
        var diagnostics = new List<ModDiagnostic>();
        foreach (var file in stagedMod.Files)
        {
            var path = file.RelativePath.Replace('\\', '/');
            if (path.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) && !path[5..].Contains('/') &&
                Fallout4PluginList.BaseMasters.Contains(path[5..], StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(ModDiagnostic.Warning("fallout4.replaces_base_master",
                    $"This mod replaces the game's own {path[5..]}; Modular keeps the original and puts it back when the mod is removed.", path));
            }
        }
        return diagnostics;
    }

    // ── Deployment ──────────────────────────────────────────────────────────

    /// <summary>
    /// After each deploy: the INI keys the game needs to load loose files and to read
    /// <c>plugins.txt</c>. Once the game has run (it makes its settings then), that is all.
    /// </summary>
    public override IReadOnlyList<string> AfterDeploymentChanged(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (ResolveMyGamesPath(installation) is not { } myGames)
            return [];

        var report = Fallout4Ini.Apply(myGames).ToList();
        if (!Fallout4Ini.HasPrefs(myGames))
        {
            report.Add("Fallout 4 hasn't been started yet, so it has no settings file. Start it once from Steam and quit at " +
                       "the main menu, then deploy again: Modular then turns on the plugin list (bEnableFileSelection).");
        }
        return report;
    }

    /// <summary>F4SE's loader, its DLL, its plugins and Address Library's data.</summary>
    public override IReadOnlyList<CoreModRule> GetCoreModRules() =>
    [
        new("f4se_loader.exe", CoreModRole.Loader, "Fallout 4 Script Extender (F4SE)"),
        new("f4se_*.dll", CoreModRole.Loader, "Fallout 4 Script Extender (F4SE), built for one game version"),
        new("Data/F4SE/Plugins/*.dll", CoreModRole.NativeCode, "F4SE plugin, built against the game executable"),
        new("Data/F4SE/Plugins/version-*.bin", CoreModRole.Dependency, "Address Library for F4SE plugins, one file per game version"),
    ];

    /// <summary>Native code is what breaks on a game update; plugins and assets keep working.</summary>
    public override IReadOnlyList<string> GetUpdateSensitivePaths() =>
    [
        "f4se_*",
        "Fallout4Launcher.exe",
        "Data/F4SE/**",
    ];

    /// <summary>The top-level <c>Data/</c> plugins a deployed mod put there.</summary>
    private static IEnumerable<string> PluginsOf(DeployedMod mod) =>
        mod.DeployedFiles
            .Where(f => string.Equals(f.RootName, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath.Replace('\\', '/'))
            .Where(p => p.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) && !p[5..].Contains('/') && Fallout4Plugin.IsPluginFile(p))
            .Select(p => p[5..]);

    internal static bool DetectRunningGame()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (GameProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // If processes cannot be listed there is nothing to go on.
        }
        return false;
    }
}
