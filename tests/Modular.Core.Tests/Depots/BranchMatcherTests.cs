using FluentAssertions;
using Modular.Core.Depots;
using Xunit;

namespace Modular.Core.Tests.Depots;

public sealed class BranchMatcherTests
{
    private static SteamBranchInfo Branch(string name, int day, params (uint Depot, ulong Manifest)[] manifests) =>
        new(name, (uint)day, DateTimeOffset.UnixEpoch.AddDays(day), null, false, manifests.ToDictionary(m => m.Depot, m => m.Manifest));

    // Held on the build before the latest patch: main depot 10, DLC depot 20.
    private static readonly Dictionary<uint, ulong> Held = new() { [1] = 10, [2] = 20 };

    [Fact]
    public void Candidates_SkipPublicOldAndPasswordBranches_NewestFirst()
    {
        var branches = new[]
        {
            Branch("public", 30, (1, 11), (2, 21)),        // the patch: moved on
            Branch("2_0_old", 5, (1, 5), (2, 6)),          // an older build: shares nothing
            Branch("2_0_hotfix", 8, (1, 7), (2, 20)),      // shares only the small DLC depot
            Branch("2_1_old", 20, (1, 10), (2, 20)),
            Branch("beta", 25, (1, 10), (2, 20)) with { PasswordRequired = true },
            Branch("2_1_previous", 22, (1, 10), (2, 20)),
        };

        BranchMatcher.Candidates(Held, branches, mainDepot: 1).Select(b => b.Name).Should().Equal("2_1_previous", "2_1_old");
    }

    [Fact]
    public void Differences_ExactMatchIsEmpty()
    {
        var publicBranch = Branch("public", 30, (1, 11), (2, 21));
        BranchMatcher.Differences(Held, Branch("2_1_old", 20, (1, 10), (2, 20)), publicBranch).Should().BeEmpty();
    }

    [Fact]
    public void Differences_ListsDepotsWithOtherManifestIds_ForTheContentCheck()
    {
        var publicBranch = Branch("public", 30, (1, 11), (2, 21));
        BranchMatcher.Differences(Held, Branch("2_1_old", 20, (1, 10), (2, 25)), publicBranch)
            .Should().Equal((2u, 20UL, 25UL));
    }

    [Fact]
    public void Differences_BranchMissingADepotPublicServes_CantServeTheBuild()
    {
        var publicBranch = Branch("public", 30, (1, 11), (2, 21));
        BranchMatcher.Differences(Held, Branch("2_1_old", 20, (1, 10)), publicBranch).Should().BeNull();
    }

    [Fact]
    public void Differences_DepotNoBranchServes_IsIgnored()
    {
        // Depot 2 was installed outside the branch system (neither public nor the branch lists it).
        var publicBranch = Branch("public", 30, (1, 11));
        BranchMatcher.Differences(Held, Branch("2_1_old", 20, (1, 10)), publicBranch).Should().BeEmpty();
    }

    [Fact]
    public void Differences_BranchSharingNoDepot_IsNoMatch()
    {
        BranchMatcher.Differences(Held, Branch("tools", 20, (9, 90)), Branch("public", 30, (9, 91))).Should().BeNull();
    }
}
