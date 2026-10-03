using System.Text.RegularExpressions;

namespace SPerformanceT.Tests;

/// <summary>
/// BepInEx throws on these characters in a section or key name, and the throw happens inside the
/// plugin's Awake, where it is logged only to Player.log. 0.3.2 shipped with an '=' in a key and
/// silently lost its whole GC section. This reads every Config.Bind call in the plugin's source.
/// </summary>
public class ConfigKeyTests
{
    private static readonly char[] Forbidden = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };

    private static readonly Regex Bind = new(
        @"\.Bind\(\s*(?<section>Section|""[^""]*"")\s*,\s*""(?<key>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    private static readonly Regex SectionConst = new(
        @"const\s+string\s+Section\s*=\s*""(?<value>[^""]*)""", RegexOptions.Compiled);

    private static string SourceRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "SPerformanceT")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "src", "SPerformanceT");
    }

    public static IEnumerable<object[]> Keys()
    {
        foreach (string file in Directory.GetFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            string section = SectionConst.Match(text).Groups["value"].Value;
            foreach (Match m in Bind.Matches(text))
            {
                string s = m.Groups["section"].Value == "Section" ? section : m.Groups["section"].Value.Trim('"');
                yield return new object[] { Path.GetFileName(file), s, Regex.Unescape(m.Groups["key"].Value) };
            }
        }
    }

    [Fact]
    public void EveryFeatureBindsSomeKeys()
    {
        // Guards the test itself: if the regex stops matching, the theory below would pass on nothing.
        Assert.True(Keys().Count() >= 10, "found only " + Keys().Count() + " Config.Bind calls");
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void ConfigNamesUseOnlyAllowedCharacters(string file, string section, string key)
    {
        Assert.False(string.IsNullOrEmpty(section), file + ": no section for '" + key + "'");
        Assert.True(section.IndexOfAny(Forbidden) < 0, file + ": section '" + section + "'");
        Assert.True(key.IndexOfAny(Forbidden) < 0, file + ": key '" + key + "'");
    }
}
