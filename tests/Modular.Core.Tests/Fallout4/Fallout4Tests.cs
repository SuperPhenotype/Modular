using System.Text;
using FluentAssertions;
using Modular.Core.Installers.Fallout4;
using Modular.Core.Modules;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Fallout4;

internal static class Plugins
{
    /// <summary>A plugin file with just a TES4 header: flags and masters.</summary>
    public static byte[] Make(uint flags = 0, params string[] masters)
    {
        var data = new MemoryStream();
        void Sub(string type, byte[] payload)
        {
            data.Write(Encoding.ASCII.GetBytes(type));
            data.Write(BitConverter.GetBytes((ushort)payload.Length));
            data.Write(payload);
        }
        Sub("HEDR", new byte[12]);
        foreach (var master in masters)
        {
            Sub("MAST", Encoding.ASCII.GetBytes(master + "\0"));
            Sub("DATA", new byte[8]);
        }
        var body = data.ToArray();
        var record = new MemoryStream();
        record.Write(Encoding.ASCII.GetBytes("TES4"));
        record.Write(BitConverter.GetBytes((uint)body.Length));
        record.Write(BitConverter.GetBytes(flags));
        record.Write(new byte[12]); // form id, VC info, version, unknown
        record.Write(body);
        return record.ToArray();
    }

    public static void Write(string dataDir, string name, uint flags = 0, params string[] masters) =>
        File.WriteAllBytes(Path.Combine(dataDir, name), Make(flags, masters));
}

public sealed class Fallout4PluginTests
{
    [Fact]
    public void Read_MastersAndFlags()
    {
        var plugin = Fallout4Plugin.Read(new MemoryStream(Plugins.Make(0x1, "Fallout4.esm", "DLCRobot.esm")), "Thing.esp");
        plugin!.Masters.Should().Equal("Fallout4.esm", "DLCRobot.esm");
        plugin.IsMaster.Should().BeTrue("the ESM flag makes it a master whatever its extension");
        plugin.IsLight.Should().BeFalse();
    }

    [Fact]
    public void Read_EslFlaggedEsp_IsLightButNotAMaster()
    {
        var plugin = Fallout4Plugin.Read(new MemoryStream(Plugins.Make(0x200)), "Small.esp");
        plugin!.IsLight.Should().BeTrue();
        plugin.IsMaster.Should().BeFalse();
    }

    [Fact]
    public void Read_EslFile_IsLightMaster()
    {
        var plugin = Fallout4Plugin.Read(new MemoryStream(Plugins.Make()), "Small.esl");
        plugin!.IsLight.Should().BeTrue();
        plugin.IsMaster.Should().BeTrue();
    }

    [Fact]
    public void Read_NotAPlugin_IsNull() =>
        Fallout4Plugin.Read(new MemoryStream(Encoding.ASCII.GetBytes("not a plugin at all, just text")), "x.esp").Should().BeNull();
}

public sealed class Fallout4PluginListTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fo4-").FullName;
    private readonly string _game;
    private readonly string _data;
    private readonly string _pluginsTxt;

    public Fallout4PluginListTests()
    {
        _game = Path.Combine(_root, "Fallout 4");
        _data = Path.Combine(_game, "Data");
        Directory.CreateDirectory(_data);
        Plugins.Write(_data, "Fallout4.esm", 0x1);
        Plugins.Write(_data, "ccBGSFO4044-HellfirePowerArmor.esl", 0x201, "Fallout4.esm");
        File.WriteAllText(Path.Combine(_game, "Fallout4.ccc"), "ccBGSFO4044-HellfirePowerArmor.esl\r\nccMissing.esl\r\n");
        _pluginsTxt = Path.Combine(_root, "AppData", "Fallout4", "plugins.txt");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ImplicitPlugins_BaseGameThenPresentCreationClubFiles() =>
        Fallout4PluginList.ImplicitPlugins(_game).Should().Equal("Fallout4.esm", "ccBGSFO4044-HellfirePowerArmor.esl");

    [Fact]
    public void Update_AddsPluginsActive_MastersBeforeOthers_AndKeepsOtherLines()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pluginsTxt)!);
        File.WriteAllText(_pluginsTxt, "# header\r\n*UserMaster.esm\r\n*UserMod.esp\r\nOffMod.esp\r\n");
        Plugins.Write(_data, "UserMaster.esm", 0x1);
        Plugins.Write(_data, "UserMod.esp");
        Plugins.Write(_data, "OffMod.esp");
        Plugins.Write(_data, "NewMaster.esm", 0x1);
        Plugins.Write(_data, "NewMod.esp", 0, "NewMaster.esm");

        var result = Fallout4PluginList.Update(_pluginsTxt, _game, ["NewMod.esp", "NewMaster.esm"]);

        result.Added.Should().BeEquivalentTo("NewMod.esp", "NewMaster.esm");
        Fallout4PluginList.Read(_pluginsTxt).Should().Equal(
            new Fallout4PluginEntry("UserMaster.esm", true),
            new Fallout4PluginEntry("NewMaster.esm", true),
            new Fallout4PluginEntry("UserMod.esp", true),
            new Fallout4PluginEntry("OffMod.esp", false),
            new Fallout4PluginEntry("NewMod.esp", true));
    }

    [Fact]
    public void Update_MovesANewPluginAfterAMasterListedBelowIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pluginsTxt)!);
        File.WriteAllText(_pluginsTxt, "*Base.esp\r\n*Other.esp\r\n");
        Plugins.Write(_data, "Base.esp");
        Plugins.Write(_data, "Other.esp");
        Plugins.Write(_data, "Patch.esp", 0, "Base.esp", "Late.esp");
        Plugins.Write(_data, "Late.esp");

        Fallout4PluginList.Update(_pluginsTxt, _game, ["Patch.esp", "Late.esp"]);

        Fallout4PluginList.Read(_pluginsTxt).Select(e => e.Name).Should().Equal("Base.esp", "Other.esp", "Late.esp", "Patch.esp");
    }

    [Fact]
    public void Update_RemovesOnlyPluginsModularAddedOnceTheirModIsGone()
    {
        Plugins.Write(_data, "Mine.esp");
        Fallout4PluginList.Update(_pluginsTxt, _game, ["Mine.esp"]);
        File.AppendAllText(_pluginsTxt, "*Theirs.esp\r\n"); // added by another tool, file missing

        File.Delete(Path.Combine(_data, "Mine.esp")); // the mod was uninstalled
        var result = Fallout4PluginList.Update(_pluginsTxt, _game, []);

        result.Removed.Should().Equal("Mine.esp");
        Fallout4PluginList.Read(_pluginsTxt).Select(e => e.Name).Should().Equal("Theirs.esp");
    }

    [Fact]
    public void Update_DoesNotListImplicitPlugins()
    {
        Fallout4PluginList.Update(_pluginsTxt, _game, ["Fallout4.esm"]).Added.Should().BeEmpty();
        File.Exists(_pluginsTxt).Should().BeFalse();
    }

    [Fact]
    public void SetOrder_RefusesAPluginBeforeItsMaster()
    {
        Plugins.Write(_data, "A.esp");
        Plugins.Write(_data, "B.esp", 0, "A.esp");
        Fallout4PluginList.Update(_pluginsTxt, _game, ["A.esp", "B.esp"]);

        var act = () => Fallout4PluginList.SetOrder(_pluginsTxt, _game, ["B.esp", "A.esp"]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*B.esp needs A.esp*");
    }

    [Fact]
    public void SetOrder_RefusesAMasterAfterAPlugin()
    {
        Plugins.Write(_data, "M.esm", 0x1);
        Plugins.Write(_data, "P.esp");
        Fallout4PluginList.Update(_pluginsTxt, _game, ["M.esm", "P.esp"]);

        var act = () => Fallout4PluginList.SetOrder(_pluginsTxt, _game, ["P.esp", "M.esm"]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*M.esm is a master*");
    }

    [Fact]
    public void SetOrder_KeepsOnOffState()
    {
        Plugins.Write(_data, "A.esp");
        Plugins.Write(_data, "B.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(_pluginsTxt)!);
        File.WriteAllText(_pluginsTxt, "*A.esp\r\nB.esp\r\n");

        Fallout4PluginList.SetOrder(_pluginsTxt, _game, ["B.esp", "A.esp"]);

        Fallout4PluginList.Read(_pluginsTxt).Should().Equal(new Fallout4PluginEntry("B.esp", false), new Fallout4PluginEntry("A.esp", true));
    }
}

public sealed class Fallout4IniTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fo4ini-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Apply_CreatesCustomIni_LeavesMissingPrefsAlone()
    {
        Fallout4Ini.Apply(_dir).Should().ContainSingle();
        File.ReadAllText(Path.Combine(_dir, Fallout4Ini.CustomIni)).Should()
            .Be("[Archive]\r\nbInvalidateOlderFiles=1\r\nsResourceDataDirsFinal=\r\n");
        File.Exists(Path.Combine(_dir, Fallout4Ini.PrefsIni)).Should().BeFalse();
    }

    [Fact]
    public void Apply_EditsInPlace_AndIsQuietOnceSet()
    {
        File.WriteAllText(Path.Combine(_dir, Fallout4Ini.CustomIni), "[Display]\r\nfFov=90\r\n\r\n[Archive]\r\nbInvalidateOlderFiles=0\r\n\r\n[Other]\r\nx=1\r\n");
        File.WriteAllText(Path.Combine(_dir, Fallout4Ini.PrefsIni), "[Launcher]\r\nuLastAspectRatio=1\r\n");

        Fallout4Ini.Apply(_dir).Should().HaveCount(2);

        File.ReadAllText(Path.Combine(_dir, Fallout4Ini.CustomIni)).Should().Be(
            "[Display]\r\nfFov=90\r\n\r\n[Archive]\r\nbInvalidateOlderFiles=1\r\nsResourceDataDirsFinal=\r\n\r\n[Other]\r\nx=1\r\n");
        File.ReadAllText(Path.Combine(_dir, Fallout4Ini.PrefsIni)).Should().Be("[Launcher]\r\nuLastAspectRatio=1\r\nbEnableFileSelection=1\r\n");
        Fallout4Ini.Apply(_dir).Should().BeEmpty();
    }
}

public sealed class Fallout4ArchiveAnalyzerTests
{
    private static List<ArchiveEntry> Entries(params string[] paths) =>
        paths.Select(p => new ArchiveEntry { FullName = p, Name = Path.GetFileName(p), Length = 10 }).ToList();

    private static Dictionary<string, string> Routes(Fallout4InstallLayout layout) =>
        layout.FileRoutes.ToDictionary(r => r.Key, r => r.Value.Destination);

    [Fact]
    public void DataFolderInArchive()
    {
        var layout = Fallout4ArchiveAnalyzer.Analyze(Entries("Data/F4SE/Plugins/mcm.dll", "Data/Interface/MCM.swf", "Data/MCM/Settings/readme.txt"));
        Routes(layout).Should().Equal(new Dictionary<string, string>
        {
            ["Data/F4SE/Plugins/mcm.dll"] = "Data/F4SE/Plugins/mcm.dll",
            ["Data/Interface/MCM.swf"] = "Data/Interface/MCM.swf",
            ["Data/MCM/Settings/readme.txt"] = "Data/MCM/Settings/readme.txt",
        });
        layout.FileRoutes["Data/F4SE/Plugins/mcm.dll"].PackageType.Should().Be(PackageTypes.Fo4F4sePlugin);
    }

    [Fact]
    public void PluginsAtTop_DocsLeftOut()
    {
        var layout = Fallout4ArchiveAnalyzer.Analyze(Entries(
            "Docs/Readme.html", "unofficial fallout 4 patch.esp", "unofficial fallout 4 patch - main.ba2", "readme.txt"));
        Routes(layout).Values.Should().BeEquivalentTo("Data/unofficial fallout 4 patch.esp", "Data/unofficial fallout 4 patch - main.ba2");
        layout.Plugins.Should().Equal("unofficial fallout 4 patch.esp");
        layout.SkippedFiles.Should().BeEquivalentTo("Docs/Readme.html", "readme.txt");
    }

    [Fact]
    public void WrapperFolderAroundDataFolders()
    {
        var layout = Fallout4ArchiveAnalyzer.Analyze(Entries("My Mod/Meshes/a.nif", "My Mod/textures/a.dds", "My Mod/MyMod.esp"));
        Routes(layout).Values.Should().BeEquivalentTo("Data/Meshes/a.nif", "Data/textures/a.dds", "Data/MyMod.esp");
    }

    [Fact]
    public void AddressLibrary()
    {
        Routes(Fallout4ArchiveAnalyzer.Analyze(Entries("F4SE/Plugins/version-1-11-240-0.bin"))).Values
            .Should().Equal("Data/F4SE/Plugins/version-1-11-240-0.bin");
    }

    [Fact]
    public void ScriptExtender_LoaderAlsoAsSteamLauncher()
    {
        var layout = Fallout4ArchiveAnalyzer.Analyze(Entries(
            "f4se_0_07_09/f4se_loader.exe", "f4se_0_07_09/f4se_1_11_240.dll", "f4se_0_07_09/f4se_readme.txt",
            "f4se_0_07_09/CustomControlMap.txt", "f4se_0_07_09/Data/Scripts/F4SE.pex", "f4se_0_07_09/src/f4se/main.cpp"));

        layout.IsScriptExtender.Should().BeTrue();
        Routes(layout).Values.Should().BeEquivalentTo("f4se_loader.exe", "f4se_1_11_240.dll", "Data/Scripts/F4SE.pex");
        layout.ExtraRoutes.Should().ContainSingle().Which.Route.Destination.Should().Be("Fallout4Launcher.exe");
    }

    [Fact]
    public void EnbAndRootFolder_GoBesideTheExecutable()
    {
        var layout = Fallout4ArchiveAnalyzer.Analyze(Entries(
            "Preset/enbseries.ini", "Preset/enbseries/effect.fx", "Preset/d3d11.dll", "Other/Root/tool.dll", "Other/Data/Thing.esp"));
        Routes(layout).Should().Equal(new Dictionary<string, string>
        {
            ["Preset/enbseries.ini"] = "enbseries.ini",
            ["Preset/enbseries/effect.fx"] = "enbseries/effect.fx",
            ["Preset/d3d11.dll"] = "d3d11.dll",
            ["Other/Root/tool.dll"] = "tool.dll",
            ["Other/Data/Thing.esp"] = "Data/Thing.esp",
        });
    }

    [Fact]
    public void SideBySideVersions_AreRefused()
    {
        var layout = Fallout4ArchiveAnalyzer.Analyze(Entries("Vanilla/Mod.esp", "Vanilla/textures/a.dds", "HD/Mod.esp", "HD/textures/a.dds"));
        layout.CanHandle.Should().BeFalse();
        layout.Refusal.Should().Contain("HD").And.Contain("Vanilla");
    }

    [Fact]
    public void NothingRecognisable_IsRefused()
    {
        var layout = Fallout4ArchiveAnalyzer.Analyze(Entries("stuff/thing.bin", "stuff/other.dat"));
        layout.CanHandle.Should().BeFalse();
        layout.Refusal.Should().NotBeNull();
    }
}

public sealed class Fallout4GameModuleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fo4mod-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Game(bool vr = false)
    {
        var game = Path.Combine(_root, vr ? "Fallout 4 VR" : "Fallout 4");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        File.WriteAllText(Path.Combine(game, vr ? "Fallout4VR.exe" : "Fallout4.exe"), "");
        if (vr)
            File.WriteAllText(Path.Combine(game, "Fallout4.exe"), "");
        Plugins.Write(Path.Combine(game, "Data"), "Fallout4.esm", 0x1);
        return game;
    }

    [Fact]
    public void Detect_ClaimsFallout4_NotVr()
    {
        var module = new Fallout4GameModule(isGameRunning: () => false);
        module.Detect(Game()).Should().NotBeNull();
        module.Detect(Game(vr: true)).Should().BeNull();
    }

    [Fact]
    public void FindUnmetDependencies_MissingMasterAndMissingF4se()
    {
        var game = Game();
        var data = Path.Combine(game, "Data");
        var prefix = Path.Combine(_root, "pfx");
        Plugins.Write(data, "Needy.esp", 0, "Fallout4.esm", "DLCCoast.esm");
        Directory.CreateDirectory(Path.Combine(data, "F4SE", "Plugins"));
        File.WriteAllText(Path.Combine(data, "F4SE", "Plugins", "Buffout4.dll"), "");
        var pluginsTxt = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "Fallout4", "plugins.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(pluginsTxt)!);
        File.WriteAllText(pluginsTxt, "*Needy.esp\r\n");
        var installation = new GameInstallation { InstallPath = game, ProtonPrefixPath = prefix };

        var unmet = new Fallout4GameModule(isGameRunning: () => false).FindUnmetDependencies(installation);

        unmet.Select(u => (u.RequiredBy, u.Identifier)).Should().BeEquivalentTo(new[]
        {
            ("Needy.esp", "DLCCoast.esm"),
            ("Buffout4.dll", "f4se"),
        });
    }
}

public sealed class Fallout4FomodInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fo4fomod-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Config = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <moduleName>Test Mod</moduleName>
          <requiredInstallFiles>
            <folder source="main" destination="" />
          </requiredInstallFiles>
          <conditionalFileInstalls>
            <patterns>
              <pattern>
                <dependencies operator="And"><fileDependency file="Fallout4.esm" state="Active"/></dependencies>
                <files><file source="patches/BasePatch.esp" destination="BasePatch.esp"/></files>
              </pattern>
              <pattern>
                <dependencies operator="And"><fileDependency file="DLCCoast.esm" state="Active"/></dependencies>
                <files><file source="patches/FarHarborPatch.esp" destination="FarHarborPatch.esp"/></files>
              </pattern>
            </patterns>
          </conditionalFileInstalls>
        </config>
        """;

    [Fact]
    public async Task InstallsIntoData_AndChecksConditionsAgainstData()
    {
        var game = Path.Combine(_root, "Fallout 4");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        Plugins.Write(Path.Combine(game, "Data"), "Fallout4.esm", 0x1);
        var archive = Path.Combine(_root, "mod.zip");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(archive), System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (path, content) in new[]
                     {
                         ("fomod/ModuleConfig.xml", Config), ("main/TestMod.esp", "p"), ("main/meshes/a.nif", "m"),
                         ("patches/BasePatch.esp", "b"), ("patches/FarHarborPatch.esp", "f"),
                     })
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        var installer = new Fallout4FomodInstaller();
        (await installer.DetectAsync(archive)).CanHandle.Should().BeTrue();
        var plan = await installer.AnalyzeAsync(archive, new InstallContext { GameDirectory = game });

        plan.InstallerId.Should().Be("fallout4-fomod");
        plan.Operations.Select(o => o.DestinationPath).Should().BeEquivalentTo("TestMod.esp", "meshes/a.nif", "BasePatch.esp");

        var staging = Path.Combine(_root, "staging");
        new Modular.Core.Installers.StagingManager(staging).RedirectPlan(plan, game, staging);
        plan.Operations.Select(o => o.DestinationPath).Should().BeEquivalentTo(
            "game/Data/TestMod.esp", "game/Data/meshes/a.nif", "game/Data/BasePatch.esp");
    }
}
