using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Fallout4;

/// <summary>
/// Installs Fallout 4 mods that have no FOMOD script: data into <c>Data/</c>, and F4SE, ENB
/// and <c>Root/</c> files beside <c>Fallout4.exe</c>. See <see cref="Fallout4ArchiveAnalyzer"/>.
/// </summary>
/// <remarks>
/// Priority 91, like the other game-specific installers: above the loose-file fallback,
/// which would put Fallout 4 files in the game folder instead of <c>Data/</c>. An archive
/// that is clearly Fallout 4's but can't be placed is still claimed, and the install stops
/// with the reason rather than falling through to that fallback.
/// </remarks>
public sealed class Fallout4ModInstaller : IModInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<Fallout4ModInstaller>? _logger;

    public Fallout4ModInstaller(IArchiveReaderFactory? archiveReaderFactory = null, ILogger<Fallout4ModInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    public string InstallerId => "fallout4";

    public string DisplayName => "Fallout 4 Installer";

    public int Priority => 91;

    public IReadOnlyList<string> SupportedGameIds { get; } = [GameIds.Fallout4, "377160"];

    public Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = _archiveReaderFactory.Open(archivePath);
            if (reader == null)
                return Task.FromResult(new InstallDetectionResult { CanHandle = false, Reason = "Unable to open archive" });

            var layout = Fallout4ArchiveAnalyzer.Analyze(reader.Entries, Path.GetFileName(archivePath));
            var claims = layout.CanHandle || layout.Refusal is not null;
            return Task.FromResult(new InstallDetectionResult
            {
                CanHandle = claims,
                Confidence = claims ? layout.Confidence : 0,
                InstallerType = layout.IsScriptExtender ? "fallout4-f4se" : "fallout4-mod",
                Reason = layout.Reason
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Fallout 4 detection failed for {Path}", archivePath);
            return Task.FromResult(new InstallDetectionResult { CanHandle = false, Reason = $"Error: {ex.Message}" });
        }
    }

    public Task<InstallPlan> AnalyzeAsync(string archivePath, InstallContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var layout = Fallout4ArchiveAnalyzer.Analyze(reader.Entries, Path.GetFileName(archivePath));
        if (!layout.CanHandle)
            throw new InvalidOperationException(layout.Refusal ?? layout.Reason);

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = context.GameDirectory,
            RootDirectories = context.ModPaths?.Roots,
            Operations = new List<FileOperation>(),
            Options = new Dictionary<string, object>
            {
                ["fallout4_plugins"] = string.Join(",", layout.Plugins),
                ["fallout4_f4se"] = layout.IsScriptExtender
            }
        };

        if (layout.SkippedFiles.Count > 0)
        {
            plan.Options["fallout4_skipped"] = string.Join(", ", layout.SkippedFiles);
            _logger?.LogInformation("Not installed (readmes, sources, outside the mod's data): {Files}", plan.Options["fallout4_skipped"]);
        }

        var byName = reader.Entries.Where(e => !e.IsDirectory)
            .GroupBy(e => e.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var routes = layout.FileRoutes.Select(r => (Entry: r.Key, Route: r.Value)).Concat(layout.ExtraRoutes);
        foreach (var (name, route) in routes)
        {
            if (!byName.TryGetValue(name, out var entry))
                continue;
            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = route.Destination,
                SizeBytes = entry.Length,
                IsCritical = route.PackageType is PackageTypes.Fo4Plugin or PackageTypes.Fo4ScriptExtender,
                TargetRoot = route.Root,
                PackageType = route.PackageType,
                RouteSource = route.RouteSource
            });
            plan.TotalBytes += entry.Length;
        }

        _logger?.LogInformation("Fallout 4 install plan: {Count} files — {Reason}", plan.Operations.Count, layout.Reason);
        return Task.FromResult(plan);
    }

    public Task<InstallResult> InstallAsync(InstallPlan plan, IProgress<InstallProgress>? progress = null, CancellationToken ct = default) =>
        ArchivePlanExecutor.ExecuteAsync(
            _archiveReaderFactory, plan, InstallerId,
            plan.Options?.TryGetValue("fallout4_f4se", out var f4se) == true && f4se is true
                ? "Installing the Fallout 4 Script Extender"
                : "Installing Fallout 4 mod",
            _logger, progress, ct);
}
