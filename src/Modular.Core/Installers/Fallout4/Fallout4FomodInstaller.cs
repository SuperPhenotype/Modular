using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Fallout4;

/// <summary>
/// FOMOD installers for Fallout 4. A Bethesda game's FOMOD script names its destinations
/// relative to <c>Data/</c>, and its file conditions (a DLC's master present, say) too, so
/// this runs the shared <see cref="FomodInstaller"/> with <c>Data/</c> as its base folder.
/// </summary>
/// <remarks>Priority 101: ahead of the shared FOMOD installer (100), which uses the game folder.</remarks>
public sealed class Fallout4FomodInstaller : IModInstaller
{
    private readonly FomodInstaller _fomod;

    public Fallout4FomodInstaller(IArchiveReaderFactory? archiveReaderFactory = null, ILogger<FomodInstaller>? logger = null)
    {
        _fomod = new FomodInstaller(archiveReaderFactory ?? new ArchiveReaderFactory(), logger);
    }

    public string InstallerId => "fallout4-fomod";

    public string DisplayName => "Fallout 4 FOMOD Installer";

    public int Priority => 101;

    public IReadOnlyList<string> SupportedGameIds { get; } = [GameIds.Fallout4, "377160"];

    public Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default) =>
        _fomod.DetectAsync(archivePath, ct);

    public async Task<InstallPlan> AnalyzeAsync(string archivePath, InstallContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var data = Fallout4PluginList.DataDirectory(context.GameDirectory);
        var dataContext = new InstallContext
        {
            GameDirectory = data,
            GameId = context.GameId,
            ModId = context.ModId,
            AllowOverwrite = context.AllowOverwrite,
            CreateBackups = context.CreateBackups,
            StagingDirectory = context.StagingDirectory,
            ConflictPolicy = context.ConflictPolicy,
            Metadata = context.Metadata,
            Game = context.Game,
            ModPaths = context.ModPaths,
            Selections = context.Selections,
        };

        // The plan's target is Data/, so staging records each file under Data/ in the game root.
        var plan = await _fomod.AnalyzeAsync(archivePath, dataContext, ct);
        plan.InstallerId = InstallerId;
        return plan;
    }

    public Task<InstallResult> InstallAsync(InstallPlan plan, IProgress<InstallProgress>? progress = null, CancellationToken ct = default) =>
        _fomod.InstallAsync(plan, progress, ct);
}
