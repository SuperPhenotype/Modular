namespace Modular.Core.Depots;

/// <summary>
/// Compares an installed build with Steam's official branches. A held game whose build a branch
/// serves can be pinned to that branch, and Steam then keeps the build by itself (design §5,
/// layer 1), with no claim to renew after each patch.
/// </summary>
public static class BranchMatcher
{
    /// <summary>
    /// Branches that may serve the installed build, newest first: not public (that one moves),
    /// not password-protected (Steam won't select those without the password), and with the
    /// installed manifest of the game's main depot (<paramref name="mainDepot"/>, its largest).
    /// Small depots such as languages are often shared between builds, so they don't count; a
    /// branch of another build is never checked further, which keeps the check cheap enough to
    /// run for every held game.
    /// </summary>
    public static IReadOnlyList<SteamBranchInfo> Candidates(
        IReadOnlyDictionary<uint, ulong> installed, IReadOnlyList<SteamBranchInfo> branches, uint mainDepot) =>
        branches
            .Where(b => b.Name != "public" && !b.PasswordRequired)
            .Where(b => installed.TryGetValue(mainDepot, out var have) && b.Manifests.TryGetValue(mainDepot, out var m) && m == have)
            .OrderByDescending(b => b.TimeUpdated ?? DateTimeOffset.MinValue)
            .ToList();

    /// <summary>
    /// The depots where <paramref name="branch"/> lists another manifest than the installed one,
    /// or null when the branch can't serve this build at all: it lacks a depot the game serves on
    /// public (so an installed DLC or language depot would have nothing to stay on). Depots
    /// neither branch lists (out-of-band ones) don't count. An empty list is an exact match; the
    /// differences left may still be manifests with identical files, which the caller compares.
    /// </summary>
    public static IReadOnlyList<(uint Depot, ulong Installed, ulong OnBranch)>? Differences(
        IReadOnlyDictionary<uint, ulong> installed, SteamBranchInfo branch, SteamBranchInfo? publicBranch)
    {
        var differences = new List<(uint, ulong, ulong)>();
        var any = false;
        foreach (var (depot, manifest) in installed)
        {
            var served = branch.Manifests.TryGetValue(depot, out var onBranch);
            if (!served)
            {
                if (publicBranch is null || publicBranch.Manifests.ContainsKey(depot))
                    return null;
                continue;
            }
            any = true;
            if (onBranch != manifest)
                differences.Add((depot, manifest, onBranch));
        }
        return any ? differences : null;
    }
}
