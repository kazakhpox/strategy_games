using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    // ---------------------------------------------------------- turn flow
    private void StartFirstTurn()
    {
        if (S.Settings.Simultaneous)
        {
            foreach (var p in Active) BeginPlayerTurn(p);
            RunAiSimultaneous();
        }
        else
        {
            S.CurrentPlayer = Active.First().Id;
            BeginPlayerTurn(P(S.CurrentPlayer));
            if (P(S.CurrentPlayer).IsAI && Active.Any(p => !p.IsAI)) { RunAi(P(S.CurrentPlayer)); AdvanceSequential(); }
        }
    }

    private void DoEndTurn(Player p)
    {
        if (S.Settings.Simultaneous)
        {
            p.EndedTurn = true;
            TryAdvanceSimultaneous();
        }
        else
        {
            ProcessPlayerEnd(p);
            AdvanceSequential();
        }
    }

    /// <summary>Ends the turn for a player who cannot do it themselves (timer expired, disconnected).</summary>
    public void ForceEndTurn(int pid)
    {
        var p = P(pid);
        if (!p.Alive || p.Neutral) return;
        if (S.Settings.Simultaneous) { if (!p.EndedTurn) DoEndTurn(p); }
        else if (S.CurrentPlayer == pid) DoEndTurn(p);
    }

    /// <summary>Advances a game without human players by one global turn (used by simulations).</summary>
    public void StepAiOnly()
    {
        if (S.Settings.Simultaneous) TryAdvanceSimultaneous();
        else
        {
            var cur = P(S.CurrentPlayer);
            if (cur.Alive && cur.IsAI) RunAi(cur);
            AdvanceSequential();
        }
    }

    private void TryAdvanceSimultaneous()
    {
        if (Active.Any(x => !x.IsAI && !x.EndedTurn)) return;
        foreach (var p in Active.ToList()) if (p.Alive) ProcessPlayerEnd(p);
        GlobalEnd();
        foreach (var p in Active.ToList()) BeginPlayerTurn(p);
        RunAiSimultaneous();
    }

    private void RunAiSimultaneous()
    {
        foreach (var ai in Active.Where(x => x.IsAI).ToList())
        {
            if (!ai.Alive) continue;
            SimpleAI.TakeTurn(this, ai.Id);
            ai.EndedTurn = true;
        }
    }

    private void RunAi(Player ai)
    {
        SimpleAI.TakeTurn(this, ai.Id);
        if (ai.Alive) ProcessPlayerEnd(ai);
    }

    private void AdvanceSequential()
    {
        bool humans = Active.Any(p => !p.IsAI);
        for (int guard = 0; guard < 64; guard++)
        {
            var order = Active.Select(p => p.Id).ToList();
            if (order.Count == 0) return;
            int next = order.FirstOrDefault(id => id > S.CurrentPlayer, -1);
            bool wrapped = next < 0;
            if (wrapped) { GlobalEnd(); next = order[0]; }
            S.CurrentPlayer = next;
            var np = P(next);
            BeginPlayerTurn(np);
            if (!np.IsAI) return;
            if (!humans && wrapped) return; // AI-only game: one global turn per call
            RunAi(np);
        }
    }

    private void BeginPlayerTurn(Player p)
    {
        p.EndedTurn = false;
        foreach (var u in UnitsOf(p.Id).ToList())
        {
            u.Moves = u.MaxMoves;
            if (u.Goto >= 0) ContinueGoto(u);
        }
    }

    private void GlobalEnd()
    {
        S.Turn++;
        if (S.Turn % Rules.CongressPeriod == 0)
        {
            // world congress: whoever received the most favor chairs it until the next session
            int winner = -1, best = 0;
            foreach (var (id, votes) in S.Votes.OrderBy(v => v.Key))
                if (votes > best && P(id).Alive) { best = votes; winner = id; }
            S.Chair = winner;
            S.Votes.Clear();
            Emit(EventKind.Congress, winner, best);
        }
        foreach (var u in UnitsOf(Neutral.Id)) u.Hp = Math.Min(100, u.Hp + 10);
        foreach (var c in CitiesOf(Neutral.Id)) c.Culture += 1;
        RecomputeBorders();
        SpreadReligion();
        foreach (var p in Active) RefreshEconomy(p);
        AntiSnowball.Recompute(this);
        foreach (var p in Active) RefreshEconomy(p);
    }

    // ------------------------------------------------------------ economy
    /// <summary>Recomputes all derived per-city and per-player rates. Idempotent and cheap.</summary>
    public void RefreshEconomy(Player p)
    {
        var cities = CitiesOf(p.Id).OrderBy(c => c.Id).ToList();
        var capital = cities.FirstOrDefault(c => c.Capital);
        var taken = new HashSet<int>();
        double sci = 0, gold = 0, bUp = 0, maint = 0;
        var cand = new List<(int tile, int score, int f, int pr, int co)>();

        // connected resources: improved inside the borders, or lying under a city
        var connected = new SortedSet<int>();
        int roads = 0;
        for (int i = 0; i < S.Tiles.Length; i++)
        {
            var t = S.Tiles[i];
            if (t.Owner != p.Id) continue;
            if (t.Road && t.CityId < 0) roads++;
            if (t.Resource != Resource.None && (Rules.ResourceWorked(t) || t.CityId >= 0) && Rules.ResourceVisible(t.Resource, p.Has))
                connected.Add((int)t.Resource);
        }
        p.Resources = connected.ToList();
        p.Luxuries = connected.Count(r => Rules.Res((Resource)r).Class == ResClass.Luxury);
        bool chair = S.Chair == p.Id;
        double faith = 0, culture = 0;
        var fx = Rules.CivicBonus(p.Civics, p.CivicLevel);
        fx.Add(Rules.NodeBonus(p.Nodes)); // custom branches use the same kinds of bonus
        p.CivicFx = fx;
        var founded = FoundedBy(p.Id);

        foreach (var c in cities)
        {
            // institutions apply everywhere, a belief only where the city follows its religion
            var bel = ReligionOf(c.Religion) is { } creed ? Rules.Beliefs[creed.Belief].Effect : null;
            int happyCap = 5 + p.Luxuries + p.Future[Rules.BranchSocial] / 2 + fx.Happy + (bel?.Happy ?? 0);
            int foodFlat = fx.Food + (bel?.Food ?? 0), comFlat = 0, culFlat = 1 + fx.Culture + (bel?.Culture ?? 0);
            double prodPct = fx.ProdPct + (bel?.ProdPct ?? 0), sciPct = fx.SciPct + (bel?.SciPct ?? 0), goldPct = fx.GoldPct + (bel?.GoldPct ?? 0);
            double culPct = fx.CulPct + (bel?.CulPct ?? 0), maintCut = 0, upkeep = 0, cityFaith = 0;
            foreach (var id in c.Buildings)
            {
                var b = Rules.Building(id);
                if (b == null) continue;
                happyCap += b.Happy; foodFlat += b.Food; comFlat += b.Commerce; culFlat += b.Culture;
                prodPct += b.ProdPct; sciPct += b.SciPct; goldPct += b.GoldPct; culPct += b.CulPct;
                maintCut = Math.Max(maintCut, b.MaintCutPct); upkeep += b.Upkeep;
                if (c.DisorderTurns == 0) cityFaith += b.Faith;
            }
            c.Happy = happyCap;
            c.Unhappy = Math.Min(c.Pop, Math.Max(0, c.Pop - happyCap) + Math.Min(2, c.Unrest / 40));
            int workers = c.Pop - c.Unhappy;

            cand.Clear();
            foreach (int t in Grid.Range(c.Tile, Rules.CityWorkRadius))
            {
                var tile = S.Tiles[t];
                if (t == c.Tile || tile.Owner != p.Id || tile.CityId >= 0 || taken.Contains(t)) continue;
                var here = At(t);
                bool blocked = false;
                for (int i = 0; i < here.Count; i++) if (here[i].Owner != p.Id && AtWar(p.Id, here[i].Owner)) { blocked = true; break; }
                if (blocked) continue;
                var (tf, tp, tc) = Rules.TileYield(tile);
                if (tf + tp + tc == 0) continue;
                cand.Add((t, tf * 4 + tp * 3 + tc * 2, tf, tp, tc));
            }
            cand.Sort((a, b) => b.score != a.score ? b.score.CompareTo(a.score) : a.tile.CompareTo(b.tile));

            var (f, pr, co) = Rules.TileYield(S.Tiles[c.Tile], true);
            c.Worked.Clear();
            for (int i = 0; i < workers && i < cand.Count; i++)
            {
                c.Worked.Add(cand[i].tile); taken.Add(cand[i].tile);
                f += cand[i].f; pr += cand[i].pr; co += cand[i].co;
            }
            f += foodFlat; co += comFlat + workers; // every working citizen also trades

            if (c.DisorderTurns > 0)
            {
                c.FoodRate = 0; c.ProdRate = 0; c.Commerce = 0; c.SciRate = 0; c.GoldRate = 0; c.CulRate = 0;
            }
            else
            {
                c.FoodRate = f - 2 * c.Pop;
                c.ProdRate = (int)(pr * p.Bonus * (1 + prodPct / 100 + 0.05 * p.Future[Rules.BranchNano]));
                co = (int)Math.Round(co * p.Bonus);
                c.Commerce = co;
                double s = co * p.Slider / 100.0;
                c.SciRate = s * (1 + sciPct / 100 + 0.05 * p.Future[Rules.BranchQuantum]) * p.SciMult;
                c.GoldRate = (co - s) * (1 + goldPct / 100 + 0.05 * p.Future[Rules.BranchSpace]);
                c.CulRate = culFlat * (1 + culPct / 100 + 0.05 * p.Future[Rules.BranchSocial]) * p.CulMult;
                if (c.Queue.Count > 0 && c.Queue[0] == Rules.Wealth) c.GoldRate += c.ProdRate;
                if (chair) { c.GoldRate *= 1.1; c.CulRate *= 1.1; } // chair of the world congress
            }
            int dist = capital == null ? 0 : Grid.Distance(capital.Tile, c.Tile);
            c.Maint = c.Capital ? 0 : (0.5 + dist / 8.0 + 0.25 * (cities.Count - 1)) * (1 - maintCut / 100);
            c.BuildingUpkeep = upkeep;
            faith += cityFaith * (1 + (fx.FaithPct + (bel?.FaithPct ?? 0)) / 100.0);
            culture += c.CulRate;
            sci += c.SciRate; gold += c.GoldRate; bUp += upkeep; maint += c.Maint;
        }

        int units = UnitsOf(p.Id).Count();
        p.Income = gold; p.SciRate = sci;
        p.UnitUpkeep = Math.Max(0, units - (4 + 3 * cities.Count));
        p.BuildingUpkeep = bUp; p.CityMaint = maint; p.RoadUpkeep = roads * 0.15;
        if (founded != null)
        {
            // the founder is paid by the faithful: faith from every follower city, gold from the foreign ones
            foreach (var c in S.Cities.Values)
            {
                if (c.Religion != founded.Id) continue;
                faith += 1;
                if (c.Owner != p.Id) gold += 1;
            }
        }
        p.Income = gold;
        p.CultureRate = culture;
        double baseCost = (p.UnitUpkeep + p.BuildingUpkeep + p.CityMaint + p.RoadUpkeep) * (1 - Math.Min(60, fx.UpkeepCutPct) / 100.0);
        p.StrainCost = baseCost * (p.UpkeepMult - 1);
        p.FaithRate = faith;
        int partners = 0; bool atWar = false;
        foreach (var o in Active)
        {
            if (o.Id == p.Id) continue;
            var rel = Rel(p.Id, o.Id);
            if (rel.AtWar) atWar = true; else if (rel.OpenBorders) partners++;
        }
        p.FavorRate = 1 + partners + (atWar ? 0 : 1) + fx.Favor;
        foreach (var b in Rules.Branches)
            p.BranchRate[b.Id] = b.FromScience * sci + b.FromCulture * culture + b.FromFaith * faith; // peace and open borders earn diplomatic favor
        p.EchoBonus = AntiSnowball.Echo(this, p);
        p.GoldRate = (int)Math.Floor(p.Income - baseCost - p.StrainCost);
    }

    private void ProcessPlayerEnd(Player p)
    {
        RefreshEconomy(p);

        foreach (var u in UnitsOf(p.Id).ToList())
        {
            if (u.Work != WorkKind.None && --u.WorkTurns <= 0)
            {
                ref var t = ref S.Tiles[u.Tile];
                if (Rules.WorkFits(Seen(p, t), u.Work))
                {
                    if (u.Work == WorkKind.Road) t.Road = true;
                    else t.Improvement = (Improvement)(int)u.Work;
                }
                u.Work = WorkKind.None; u.WorkTurns = 0;
            }
            if (u.Hp < 100 && u.Moves == u.MaxMoves)
            {
                var city = CityAt(u.Tile);
                u.Hp = Math.Min(100, u.Hp + (city != null && city.Owner == p.Id ? 25 : S.Tiles[u.Tile].Owner == p.Id ? 15 : 10));
            }
        }

        var capital = CitiesOf(p.Id).FirstOrDefault(c => c.Capital);
        int cityCount = CitiesOf(p.Id).Count();
        foreach (var c in CitiesOf(p.Id).OrderBy(c => c.Id).ToList())
        {
            if (c.Owner != p.Id) continue;
            if (c.DisorderTurns > 0) c.DisorderTurns--;
            else
            {
                c.Food += c.FoodRate;
                int need = Rules.FoodToGrow(c.Pop);
                if (c.Food >= need) { c.Pop++; c.Food -= need; }
                else if (c.Food < 0) { if (c.Pop > 1) c.Pop--; c.Food = 0; }
                ProcessProduction(p, c);
                c.Culture += Math.Max(1, (int)Math.Round(c.CulRate));
            }
            ProcessUnrest(p, c, capital, cityCount);
        }

        double sci = p.SciRate + p.EchoBonus + p.SciFrac;
        int add = (int)Math.Floor(sci);
        p.SciFrac = sci - add;
        p.ResearchProgress += add;
        if (p.Research != null)
        {
            var t = Rules.Tech(p.Research);
            if (t == null) p.Research = null;
            else
            {
                int cost = AntiSnowball.TechCost(this, p, t);
                if (p.ResearchProgress >= cost) { p.ResearchProgress -= cost; Learn(p, t); }
            }
        }

        p.Faith += (int)p.FaithRate;
        foreach (var b in Rules.Branches)
        {
            int have = p.BranchProgress.GetValueOrDefault(b.Id) + (int)Math.Round(p.BranchRate.GetValueOrDefault(b.Id));
            if (p.BranchCurrent.TryGetValue(b.Id, out var cur))
            {
                var node = Rules.Node(cur);
                if (node == null || p.Nodes.Contains(cur)) p.BranchCurrent.Remove(b.Id); // the mod changed under a saved game
                else if (have >= node.Cost)
                {
                    have -= node.Cost;
                    p.Nodes.Add(node.Id);
                    p.BranchCurrent.Remove(b.Id);
                    Emit(EventKind.NodeDone, s: node.Id, only: p.Id);
                }
            }
            p.BranchProgress[b.Id] = have;
        }
        p.CivicProgress += (int)Math.Round(p.CultureRate);
        if (p.Civic != null)
        {
            var civic = Rules.Civic(p.Civic);
            if (civic == null) p.Civic = null;
            else if (p.CivicProgress >= civic.Cost)
            {
                p.CivicProgress -= civic.Cost;
                if (civic.Level > 0) p.CivicLevel = Math.Max(p.CivicLevel, civic.Level); else p.Civics.Add(civic.Id);
                p.Civic = null;
                Emit(EventKind.CivicDone, s: civic.Id, only: p.Id);
            }
        }
        p.Favor += (int)p.FavorRate;
        p.Gold += p.GoldRate;
        if (p.Gold < 0)
        {
            p.Gold = 0;
            if (p.Slider > 0)
            {
                p.Slider = Math.Max(0, p.Slider - 20);
                Emit(EventKind.Bankrupt, s: "финансирование науки урезано", only: p.Id);
            }
            else
            {
                var victim = UnitsOf(p.Id).Where(IsMilitary).OrderBy(u => UnitStr(u)).ThenBy(u => u.Id).FirstOrDefault();
                if (victim != null)
                {
                    RemoveUnit(victim);
                    Emit(EventKind.Bankrupt, s: $"распущен юнит ({victim.Def.Name})", only: p.Id);
                }
            }
        }
        RefreshEconomy(p);
    }

    public void Learn(Player p, TechDef t)
    {
        if (t.Branch >= 0) p.Future[t.Branch] = Math.Max(p.Future[t.Branch], t.Level);
        else p.Known.Add(t.Id);
        p.TechPoints += t.Cost;
        if (p.Research == t.Id) p.Research = null;
        // newly understood strategic resources appear on every tile the player has already explored
        for (int i = 0; i < p.KnownTiles.Length && i < S.Tiles.Length; i++)
        {
            var r = S.Tiles[i].Resource;
            if (p.KnownTiles[i] != 0 && r != Resource.None && Rules.Res(r).Reveal == t.Id)
                p.KnownTiles[i] = (p.KnownTiles[i] & ~TileData.ResourceMask) | ((ulong)r << 6);
        }
        Emit(EventKind.TechDone, s: t.Id, only: p.Id);
    }

    private void ProcessProduction(Player p, City c)
    {
        if (c.Queue.Count == 0) { c.Prod = Math.Min(c.Prod + c.ProdRate, 60); return; }
        string item = c.Queue[0];
        if (item == Rules.Wealth) return; // already converted to gold in RefreshEconomy
        if (!CanBuild(p, c, item)) { c.Queue.RemoveAt(0); return; }
        c.Prod += c.ProdRate;
        int cost = Rules.ItemCost(item);
        if (c.Prod < cost) return;
        if (item.StartsWith("u:"))
        {
            int type = Rules.UnitIndex(item[2..]);
            if (Rules.Units[type].Role == UnitRole.Settler)
            {
                if (c.Pop < 2) { c.Prod = cost; return; } // waits until the city can spare a citizen
                c.Pop--;
            }
            AddUnit(p.Id, type, c.Tile).Moves = 0;
        }
        else c.Buildings.Add(item[2..]);
        c.Prod -= cost;
        c.Queue.RemoveAt(0);
        Emit(EventKind.BuildDone, c.Id, s: item, only: p.Id);
        if (c.Queue.Count == 0) Emit(EventKind.IdleCity, c.Id, s: c.Name, only: p.Id);
    }

    /// <summary>"Social unrest": cities of a runaway leader accumulate unrest, leading to protests and separatism.</summary>
    private void ProcessUnrest(Player p, City c, City? capital, int cityCount)
    {
        int dist = capital == null ? 0 : Grid.Distance(capital.Tile, c.Tile);
        double calm = 3 + p.Future[Rules.BranchSocial] + p.CivicFx.Calm + (ReligionOf(c.Religion) is { } creed ? Rules.Beliefs[creed.Belief].Effect.Calm : 0);
        foreach (var id in c.Buildings) calm += Rules.Building(id)?.Calm ?? 0;
        double pressure = Bal.UnrestPerPressure * p.Pressure * (0.6 + dist / 8.0) + Math.Max(0, c.Pop - c.Happy);
        c.Unrest = (int)Math.Clamp(c.Unrest + pressure - calm, 0, 100);
        if (c.Unrest < 100 || c.DisorderTurns > 0) return;

        bool secede = !c.Capital && cityCount >= 3 && p.Pressure >= Bal.SecessionPressure
            && dist >= Bal.SecessionMinDistance && Rand(100) < Bal.SecessionChancePct;
        if (secede)
        {
            int best = 3;
            for (int i = 0; i < Rules.Units.Length; i++)
                if (Rules.Units[i].Role == UnitRole.Military && p.Has(Rules.Units[i].Tech) && Rules.Units[i].Str > Rules.Units[best].Str) best = i;
            TransferCity(c, Neutral.Id, false);
            for (int i = 0; i < 2; i++) AddUnit(Neutral.Id, best, c.Tile).Fortified = true;
        }
        else
        {
            c.DisorderTurns = 3;
            c.Unrest = 60;
            Emit(EventKind.Protest, c.Id, s: c.Name, only: p.Id);
        }
    }
}
