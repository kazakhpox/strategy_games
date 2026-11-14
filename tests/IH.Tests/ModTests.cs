using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class ModTests
{
    private static string? Cmd(Game g, int pid, CmdType t, int a = 0, int b = 0, int c = 0, string s = "") => g.Execute(pid, new GameCommand(t, a, b, c, s));

    private const string Necromancy = """
        {
          // comments and trailing commas are allowed
          "Id": "necromancy", "Name": "Некромантия", "FromFaith": 1.0, "FromScience": 0.2,
          "Nodes": [
            { "Id": "nc_bones", "Name": "Костяные обереги", "Cost": 40, "Effect": { "Calm": 1 } },
            { "Id": "nc_rites", "Name": "Тёмные обряды", "Cost": 90, "Requires": ["nc_bones", "sh_spirits", "priesthood", "c_rites"], "Effect": { "FaithPct": 20, "StrPct": 5 } },
          ],
        }
        """;

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ih-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Built_in_branches_are_valid_and_cross_linked()
    {
        var all = Rules.DefaultBranches();
        Assert.Equal(new[] { "shamanism", "magic" }, all.Select(b => b.Id));
        foreach (var b in all) Assert.Empty(ModLoader.Validate(b, all));
        var magic = all.Single(b => b.Id == "magic");
        // magic leans on science, institutions and on the shamanism branch
        Assert.Contains("writing", magic.Nodes.Single(n => n.Id == "mg_runes").Requires);
        Assert.Contains("sh_herbs", magic.Nodes.Single(n => n.Id == "mg_alchemy").Requires);
        Assert.Contains("c_humanism", magic.Nodes.Single(n => n.Id == "mg_arcane").Requires);
        Assert.Equal("Травничество (Шаманство)", Rules.RequirementName("sh_herbs"));
        Assert.Equal("Письменность (наука)", Rules.RequirementName("writing"));
        Assert.Equal("Обряды предков (институт)", Rules.RequirementName("c_rites"));
    }

    [Fact]
    public void Mod_files_load_override_and_can_disable_branches()
    {
        string dir = TempDir();
        try
        {
            var log = new List<string>();
            Assert.Equal(2, ModLoader.Load(Path.Combine(dir, "missing"), log.Add).Count);      // no folder: built-ins only

            File.WriteAllText(Path.Combine(dir, "necromancy.json"), Necromancy);
            var loaded = ModLoader.Load(dir, log.Add);
            Assert.Equal(3, loaded.Count);
            var nc = loaded.Single(b => b.Id == "necromancy");
            Assert.Equal("Тёмные обряды", nc.Nodes[1].Name);
            Assert.Equal(20, nc.Nodes[1].Effect.FaithPct);
            Assert.Equal(1.0f, nc.FromFaith);
            Assert.Empty(nc.Nodes[0].Requires);                                                  // omitted fields get defaults
            Assert.Contains(log, l => l.Contains("Некромантия"));

            // the exported examples are themselves loadable, and a same-id file replaces the built-in
            ModLoader.Export(dir);
            Assert.True(File.Exists(Path.Combine(dir, "magic.json")));
            var magic = ModLoader.Parse(File.ReadAllText(Path.Combine(dir, "magic.json")));
            magic.Name = "Чародейство"; magic.Nodes[0].Cost = 7;
            File.WriteAllText(Path.Combine(dir, "magic.json"), ModLoader.ToJson(magic));
            loaded = ModLoader.Load(dir);
            Assert.Equal(3, loaded.Count);
            Assert.Equal("Чародейство", loaded.Single(b => b.Id == "magic").Name);
            Assert.Equal(7, loaded.Single(b => b.Id == "magic").Nodes[0].Cost);

            // switching shamanism off also disables what depends on it, with an explanation
            var sham = ModLoader.Parse(File.ReadAllText(Path.Combine(dir, "shamanism.json")));
            sham.Enabled = false;
            File.WriteAllText(Path.Combine(dir, "shamanism.json"), ModLoader.ToJson(sham));
            log.Clear();
            loaded = ModLoader.Load(dir, log.Add);
            Assert.DoesNotContain(loaded, b => b.Enabled);
            Assert.Contains(log, l => l.Contains("отключена") && l.Contains("Чародейство"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("{ not json", "JSON")]
    [InlineData("""{ "Id": "x", "Name": "X", "FromFaith": 1, "Nodes": [] }""", "нет узлов")]
    [InlineData("""{ "Id": "x", "Name": "X", "Nodes": [ { "Id": "a", "Name": "A", "Cost": 5 } ] }""", "ни от чего не растёт")]
    [InlineData("""{ "Id": "x", "Name": "X", "FromFaith": 1, "Nodes": [ { "Id": "a", "Name": "A", "Cost": 0 } ] }""", "Cost")]
    [InlineData("""{ "Id": "x", "Name": "X", "FromFaith": 1, "Nodes": [ { "Id": "a", "Name": "A", "Cost": 5, "Requires": ["wrting"] } ] }""", "«wrting» не найдено")]
    [InlineData("""{ "Id": "x", "Name": "X", "FromFaith": 1, "Nodes": [ { "Id": "writing", "Name": "A", "Cost": 5 } ] }""", "совпадает с технологией")]
    [InlineData("""{ "Id": "x", "Name": "X", "FromFaith": 1, "Nodes": [ { "Id": "sh_herbs", "Name": "A", "Cost": 5 } ] }""", "уже есть в другой ветке")]
    [InlineData("""{ "Id": "x", "Name": "X", "FromFaith": 1, "Nodes": [ { "Id": "a", "Name": "A", "Cost": 5, "Requires": ["b"] }, { "Id": "b", "Name": "B", "Cost": 5, "Requires": ["a"] } ] }""", "круговой")]
    public void Broken_mod_files_are_skipped_with_a_reason(string json, string expected)
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), json);
            var log = new List<string>();
            var loaded = ModLoader.Load(dir, log.Add);
            Assert.Equal(2, loaded.Count);                                 // the game still starts with the built-ins
            Assert.Contains(log, l => l.Contains("bad.json"));
            Assert.Contains(log, l => l.Contains(expected));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Branches_advance_in_parallel_and_respect_requirements_from_other_trees()
    {
        var g = GameTests.New(2, 0);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Settler).Id));
        var p = g.P(0);
        Assert.NotNull(Cmd(g, 0, CmdType.SetNode, s: "nope"));
        Assert.Contains("Письменность", Cmd(g, 0, CmdType.SetNode, s: "mg_runes"));           // needs a technology
        Assert.Contains("Духи предков", Cmd(g, 0, CmdType.SetNode, s: "sh_herbs"));           // needs a node and a tech
        Assert.Null(Cmd(g, 0, CmdType.SetNode, s: "sh_spirits"));
        g.RefreshEconomy(p);
        Assert.True(p.BranchRate["shamanism"] > 0);                                            // palace faith + culture
        int happy = g.CitiesOf(0).Single().Happy;
        for (int i = 0; i < 30 && !p.Nodes.Contains("sh_spirits"); i++) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.Contains("sh_spirits", p.Nodes);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.NodeDone && e.Dto.S == "sh_spirits");
        g.RefreshEconomy(p);
        Assert.Equal(happy + 1, g.CitiesOf(0).Single().Happy);                                 // its bonus applies
        Assert.NotNull(Cmd(g, 0, CmdType.SetNode, s: "sh_spirits"));                           // already known

        // requirements from science, institutions and the other branch unlock the cross-linked node
        Assert.NotNull(Cmd(g, 0, CmdType.SetNode, s: "mg_alchemy"));
        p.Known.Add("writing"); p.Known.Add("agriculture"); p.Nodes.Add("mg_runes");
        Assert.Contains("Травничество", Cmd(g, 0, CmdType.SetNode, s: "mg_alchemy"));
        p.Nodes.Add("sh_herbs");
        Assert.Null(Cmd(g, 0, CmdType.SetNode, s: "mg_alchemy"));
        // both branches progress at the same time, next to science and institutions
        Assert.Null(Cmd(g, 0, CmdType.SetNode, s: "sh_totems") is null ? null : Cmd(g, 0, CmdType.SetCivic, s: "c_tribal"));
        var me = g.BuildView(0).Me;
        Assert.Equal(Rules.Branches.Count, me.BranchCurrent.Length);
        Assert.Equal("mg_alchemy", me.BranchCurrent[Rules.Branches.ToList().FindIndex(b => b.Id == "magic")]);
        Assert.Contains("sh_spirits", me.Nodes);

        // definitions travel to clients inside the game start message
        var start = new GameStartMsg { W = 10, H = 10, Branches = Rules.Branches.ToArray() };
        var bytes = Codec.Encode(MsgType.GameStart, start);
        var back = Codec.Decode<GameStartMsg>(bytes, 0, bytes.Length);
        Assert.Equal(Wire.ToBytes(start), Wire.ToBytes(back));
        Assert.Equal("Алхимия", back.Branches.Single(b => b.Id == "magic").Nodes[1].Name);
        Assert.Equal(10, back.Branches.Single(b => b.Id == "magic").Nodes[1].Effect.GoldPct);
    }
}
