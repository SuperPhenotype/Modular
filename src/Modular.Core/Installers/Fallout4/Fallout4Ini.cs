using System.Text;

namespace Modular.Core.Installers.Fallout4;

/// <summary>
/// The INI settings Fallout 4 needs before mods load as installed. Without them the game
/// prefers its own <c>.ba2</c> archives over loose files (so most texture and mesh mods do
/// nothing), and ignores <c>plugins.txt</c>.
/// </summary>
public static class Fallout4Ini
{
    /// <summary>The folder under Documents holding the game's INI files and saves.</summary>
    public const string Folder = "My Games/Fallout4";

    public const string CustomIni = "Fallout4Custom.ini";
    public const string PrefsIni = "Fallout4Prefs.ini";

    /// <summary>
    /// Sets the keys in <c>Fallout4Custom.ini</c> (created if missing) and, when the game has
    /// written <c>Fallout4Prefs.ini</c>, the one there. Other lines are kept as they are.
    /// </summary>
    /// <param name="myGamesFolder">The game's <c>My Games/Fallout4</c> folder.</param>
    /// <returns>Lines for the deploy report; empty when everything was already set.</returns>
    public static IReadOnlyList<string> Apply(string myGamesFolder)
    {
        var report = new List<string>();
        var custom = Path.Combine(myGamesFolder, CustomIni);
        var changed = SetValues(custom, createIfMissing: true,
            ("Archive", "bInvalidateOlderFiles", "1"),
            ("Archive", "sResourceDataDirsFinal", ""));
        if (changed)
            report.Add($"{CustomIni}: loose files now override the game's archives (bInvalidateOlderFiles=1, sResourceDataDirsFinal=).");

        // Fallout4Prefs.ini holds the hardware settings the launcher detects on first run; a
        // file Modular made up would skip that, so it is only edited once the game made it.
        var prefs = Path.Combine(myGamesFolder, PrefsIni);
        if (File.Exists(prefs))
        {
            if (SetValues(prefs, createIfMissing: false, ("Launcher", "bEnableFileSelection", "1")))
                report.Add($"{PrefsIni}: the game now loads the plugins in plugins.txt (bEnableFileSelection=1).");
        }
        return report;
    }

    /// <summary>Whether the game has made its settings yet (it does on first launch).</summary>
    public static bool HasPrefs(string myGamesFolder) => File.Exists(Path.Combine(myGamesFolder, PrefsIni));

    /// <summary>Sets <c>[section] key=value</c> entries in place; returns whether the file changed.</summary>
    public static bool SetValues(string path, bool createIfMissing, params (string Section, string Key, string Value)[] values)
    {
        if (!File.Exists(path) && !createIfMissing)
            return false;
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var original = string.Join("\n", lines);

        foreach (var (section, key, value) in values)
        {
            var sectionStart = lines.FindIndex(l => IsSection(l, section));
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                    lines.Add(string.Empty);
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
                continue;
            }

            var sectionEnd = lines.FindIndex(sectionStart + 1, l => l.TrimStart().StartsWith('['));
            if (sectionEnd < 0)
                sectionEnd = lines.Count;
            var keyLine = lines.FindIndex(sectionStart + 1, sectionEnd - sectionStart - 1, l => IsKey(l, key));
            if (keyLine >= 0)
            {
                lines[keyLine] = $"{key}={value}";
            }
            else
            {
                // After the section's last non-blank line, so a blank separator stays in place.
                var insertAt = sectionEnd;
                while (insertAt > sectionStart + 1 && lines[insertAt - 1].Trim().Length == 0)
                    insertAt--;
                lines.Insert(insertAt, $"{key}={value}");
            }
        }

        if (string.Join("\n", lines) == original)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".modular.tmp";
        File.WriteAllText(temporary, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
        return true;
    }

    private static bool IsSection(string line, string section) =>
        string.Equals(line.Trim(), $"[{section}]", StringComparison.OrdinalIgnoreCase);

    private static bool IsKey(string line, string key)
    {
        var trimmed = line.TrimStart();
        var eq = trimmed.IndexOf('=');
        return eq > 0 && string.Equals(trimmed[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase);
    }
}
