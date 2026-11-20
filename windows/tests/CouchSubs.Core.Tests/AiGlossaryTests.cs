// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

/// <summary>Fork: yapay zekâ çevirisinin oyun sözlüğü.</summary>
public class AiGlossaryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiglossary-" + Guid.NewGuid());

    public AiGlossaryTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void GameFileAppliesOnlyToItsGame()
    {
        File.WriteAllText(Path.Combine(dir, "gta6.json"),
            """{ "game": "GTA 6", "terms": { "bro": "kanka" }, "keep": ["Vice City"] }""");
        File.WriteAllText(Path.Combine(dir, "all.json"), """{ "terms": { "cops": "aynasızlar" } }""");
        var glossary = new AiGlossary(dir);

        var gta = glossary.For("Grand Theft Auto VI");
        var terms = AiGlossary.Terms(gta, ["Yo bro, the cops are in Vice City!"]);
        Assert.Equal([("bro", "kanka"), ("cops", "aynasızlar"), ("Vice City", "Vice City")], terms);

        var other = AiGlossary.Terms(glossary.For("Red Dead Redemption 2"), ["Yo bro, the cops!"]);
        Assert.Equal([("cops", "aynasızlar")], other);
        Assert.Empty(AiGlossary.Terms(new AiGlossary(Path.Combine(dir, "yok")).For("GTA 6"), ["bro"]));
    }

    [Fact]
    public void BrokenFileIsSkipped()
    {
        File.WriteAllText(Path.Combine(dir, "a.json"), "{ bozuk");
        File.WriteAllText(Path.Combine(dir, "b.json"), """{ "terms": { "bro": "kanka" } }""");
        Assert.Equal([("bro", "kanka")], AiGlossary.Terms(new AiGlossary(dir).For(null), ["bro"]));
    }
}
