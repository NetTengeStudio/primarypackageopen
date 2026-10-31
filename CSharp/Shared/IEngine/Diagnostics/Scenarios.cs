using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace IEngine.Diagnostics;

static partial class Scenarios
{
    abstract class Scenario
    {
        public abstract string Name { get; }
        public virtual void Start() { }
        public abstract void Tick(float dt);
        public virtual void Stop() { }
        public long Events;
    }

    static readonly List<Scenario> running = new();
    static Random rng = new(1);
    static List<Hull> hulls = new();
    static float sinceStart;

    public static void Arm() => Probes.SimTickEnded += Tick;
    public static void Disarm() => Probes.SimTickEnded -= Tick;

    static void Tick()
    {
        if (running.Count == 0 || GameMain.GameSession is not { IsRunning: true }) { return; }
        float dt = (float)Timing.Step;
        sinceStart += dt;
        for (int i = 0; i < running.Count; i++)
        {
            try { running[i].Tick(dt); }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"IEngine scenario {running[i].Name} failed", e);
                running.RemoveAt(i--);
            }
        }
    }

    public static string Command(string[] args)
    {
        if (args.Length == 0 || args[0] == "list") { return "ie_scenario fire|flood|explosions|gunfire|npcs|physics|walk|stop [key=value ...]; running: " + string.Join(", ", running.Select(r => $"{r.Name}({r.Events}{(r is Gunfire g ? $", pool misses {g.PoolMisses}" : "")})")); }
        if (args[0] == "stop")
        {
            foreach (Scenario r in running) { r.Stop(); }
            running.Clear();
            return "scenarios stopped";
        }
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string a in args.Skip(1)) { if (a.Split('=', 2) is { Length: 2 } kv) { p[kv[0]] = kv[1]; } }
        int I(string k, int d) => p.TryGetValue(k, out string v) ? int.Parse(v, CultureInfo.InvariantCulture) : d;
        float F(string k, float d) => p.TryGetValue(k, out string v) ? float.Parse(v, CultureInfo.InvariantCulture) : d;
        string S(string k, string d) => p.TryGetValue(k, out string v) ? v : d;
        rng = new Random(I("seed", 1));
        hulls = Hull.HullList.Where(h => h.Submarine == Submarine.MainSub && h.Volume > 0).OrderBy(h => h.ID).ToList();
        if (hulls.Count == 0) { return "no hulls in main sub"; }
        Scenario s = args[0] switch
        {
            "fire" => new Fire(I("count", 6)),
            "flood" => new Flood(I("count", 4), F("fill", 0.6f)),
            "explosions" => new Explosions(F("period", 2f), F("range", 300f), F("damage", 30f)),
            "gunfire" => new Gunfire(I("shooters", 6), F("rate", 8f), S("ammo", "9mm_round"), I("targets", 6), F("near", 30f), I("pool", 0), I("mortal", 0) == 1),
            "npcs" => new Npcs(I("count", 10)),
            "physics" => new Physics(I("count", 60), S("item", "steel")),
            "walk" => new Walk(F("speed", 3f)),
            _ => null
        };
        if (s == null) { return $"unknown scenario {args[0]}"; }
        s.Start();
        running.Add(s);
        return $"scenario {s.Name} started ({string.Join(" ", args.Skip(1))})";
    }

    static Vector2 Floor(Hull h) => new(h.WorldRect.Center.X, h.WorldRect.Y - h.WorldRect.Height + 40);
    static Vector2 Center(Hull h) => new(h.WorldRect.Center.X, h.WorldRect.Y - h.WorldRect.Height / 2);

    static Vector2 Focus()
    {
#if CLIENT
        if (Character.Controlled is { } controlled) { return controlled.WorldPosition; }
#endif
        foreach (Character c in Character.CharacterList) { if (c.IsHuman && !c.IsDead) { return c.WorldPosition; } }
        return Center(hulls[0]);
    }

    static List<Hull> Near(float meters, int count)
    {
        Vector2 f = Focus();
        return hulls.OrderBy(h => Vector2.DistanceSquared(Center(h), f)).ThenBy(h => h.ID).Where(h => Vector2.Distance(Center(h), f) <= meters * 100).Take(count).ToList();
    }

    static bool Authoritative => GameMain.NetworkMember is not { IsClient: true };

    sealed class Fire : Scenario
    {
        readonly int count;
        public Fire(int count) => this.count = count;
        public override string Name => "fire";
        public override void Start()
        {
            if (!Authoritative) { return; }
            for (int i = 0; i < count; i++)
            {
                Hull h = hulls[(i * 7919) % hulls.Count];
                new FireSource(Floor(h), h);
                Events++;
            }
        }
        public override void Tick(float dt) { }
    }

    sealed class Flood : Scenario
    {
        readonly int count;
        readonly float fill;
        public Flood(int count, float fill) { this.count = count; this.fill = fill; }
        public override string Name => "flood";
        public override void Start()
        {
            if (!Authoritative) { return; }
            var walls = Structure.WallList.Where(w => w.Submarine == Submarine.MainSub && w.HasBody && !w.IsPlatform && w.SectionCount > 1).OrderBy(w => w.ID).ToList();
            for (int i = 0; i < count; i++)
            {
                Hull h = hulls[(i * 104729) % hulls.Count];
                h.WaterVolume = h.Volume * fill;
                Events++;
                if (walls.Count == 0) { continue; }
                Structure w = walls[(i * 15485863) % walls.Count];
                int section = w.SectionCount / 2;
                w.AddDamage(section, w.MaxHealth * 2, null, emitParticles: true);
                Events++;
            }
        }
        public override void Tick(float dt) { }
    }

    sealed class Explosions : Scenario
    {
        readonly float period, range, damage;
        float timer;
        int next;
        public Explosions(float period, float range, float damage) { this.period = period; this.range = range; this.damage = damage; }
        public override string Name => "explosions";
        public override void Tick(float dt)
        {
            if (!Authoritative || (timer += dt) < period) { return; }
            timer -= period;
            List<Hull> near = Near(40, 12);
            if (near.Count == 0) { return; }
            Hull h = near[next++ % near.Count];
            new Explosion(range, 10f, damage, damage, damage * 2).Explode(Center(h), null);
            Events++;
        }
    }

    sealed class Gunfire : Scenario
    {
        readonly int shooterCount, targetCount;
        readonly float rate, near;
        readonly string ammo;
        ItemPrefab prefab;
        readonly List<Vector2> shooters = new();
        readonly List<Character> targets = new();
        readonly List<(Character Body, float At)> corpses = new();
        float clock;
        readonly List<Vector2> targetSpots = new();
        float budget;
        int shot;
        readonly int poolSize;
        readonly bool mortal;
        readonly Queue<Item> pool = new();
        public long PoolMisses;
        public Gunfire(int shooters, float rate, string ammo, int targets, float near, int pool, bool mortal) { shooterCount = shooters; this.rate = rate; this.ammo = ammo; targetCount = targets; this.near = near; poolSize = pool; this.mortal = mortal; }
        public override string Name => "gunfire";
        public override void Start()
        {
            prefab = ItemPrefab.Prefabs.Find(p => p.Identifier == ammo);
            if (prefab == null || !prefab.ConfigElement.Elements().Any(e => e.Name.ToString().Equals("Projectile", StringComparison.OrdinalIgnoreCase))) { throw new Exception($"{ammo} is not a projectile item"); }
            List<Hull> spots = Near(near, shooterCount + targetCount);
            for (int i = 0; i < spots.Count; i++)
            {
                if (i % 2 == 0 && shooters.Count < shooterCount) { shooters.Add(Center(spots[i])); }
                else { targetSpots.Add(Floor(spots[i])); }
            }
            if (targetSpots.Count == 0) { targetSpots.Add(Focus()); }
            for (int i = 0; i < poolSize && shooters.Count > 0; i++)
            {
                Entity.Spawner?.AddItemToSpawnQueue(prefab, shooters[i % shooters.Count], onSpawned: item =>
                {
                    item.HiddenInGame = true;
                    if (item.body != null) { item.body.Enabled = false; }
                    pool.Enqueue(item);
                });
            }
        }

        void Fire(Item item, float angle)
        {
            item.HiddenInGame = false;
            item.GetComponent<Projectile>()?.Shoot(null, item.SimPosition, item.SimPosition, angle, null, createNetworkEvent: true);
        }
        public override void Tick(float dt)
        {
            if (!Authoritative || shooters.Count == 0) { return; }
            clock += dt;
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                if (targets[i].Removed) { targets.RemoveAt(i); continue; }
                if (!targets[i].IsDead) { continue; }
                corpses.Add((targets[i], clock));
                targets.RemoveAt(i);
            }
            while (corpses.Count > 0 && clock - corpses[0].At > 3f)
            {
                if (!corpses[0].Body.Removed) { Entity.Spawner?.AddEntityToRemoveQueue(corpses[0].Body); }
                corpses.RemoveAt(0);
            }
            while (targets.Count < targetCount && Entity.Spawner != null)
            {
                Vector2 at = targetSpots[(shot + targets.Count) % targetSpots.Count];
                var c = Character.Create(CharacterPrefab.HumanSpeciesName, at, $"ie_target_{shot}_{targets.Count}", hasAi: false, spawnInitialItems: false);
                if (c == null) { break; }
                c.TeamID = CharacterTeamType.Team2;
                if (!mortal) { c.CharacterHealth.Unkillable = true; }
                targets.Add(c);
                Events++;
            }
            budget += rate * shooters.Count * dt;
            while (budget >= 1f)
            {
                budget -= 1f;
                Vector2 from = shooters[shot % shooters.Count];
                Vector2 to = targets.Count > 0 ? targets[shot % targets.Count].WorldPosition : targetSpots[shot % targetSpots.Count];
                float angle = MathF.Atan2(to.Y - from.Y, to.X - from.X) + (float)(rng.NextDouble() - 0.5) * 0.05f;
                shot++;
                Events++;
                if (poolSize > 0)
                {
                    Item round = null;
                    while (pool.TryDequeue(out Item candidate)) { if (!candidate.Removed) { round = candidate; break; } }
                    if (round == null) { PoolMisses++; continue; }
                    round.SetTransform(FarseerPhysics.ConvertUnits.ToSimUnits(from - (round.Submarine?.Position ?? Microsoft.Xna.Framework.Vector2.Zero)), 0);
                    Fire(round, angle);
                    continue;
                }
                Entity.Spawner?.AddItemToSpawnQueue(prefab, from, onSpawned: item => Fire(item, angle));
            }
        }
    }

    sealed class Npcs : Scenario
    {
        readonly int count;
        public Npcs(int count) => this.count = count;
        public override string Name => "npcs";
        public override void Start()
        {
            if (!Authoritative) { return; }
            for (int i = 0; i < count; i++)
            {
                Hull h = hulls[(i * 31) % hulls.Count];
                var c = Character.Create(CharacterPrefab.HumanSpeciesName, Floor(h), $"ie_npc_{i}", hasAi: true);
                if (c != null) { c.TeamID = CharacterTeamType.Team1; Events++; }
            }
        }
        public override void Tick(float dt) { }
    }

    sealed class Physics : Scenario
    {
        readonly int count;
        readonly string item;
        public Physics(int count, string item) { this.count = count; this.item = item; }
        public override string Name => "physics";
        public override void Start()
        {
            if (!Authoritative) { return; }
            var prefab = ItemPrefab.Prefabs.Find(p => p.Identifier == item) ?? throw new Exception($"no item {item}");
            List<Hull> near = Near(40, 10);
            for (int i = 0; i < count; i++)
            {
                Hull h = near[i % near.Count];
                Vector2 pos = Center(h) + new Vector2((float)(rng.NextDouble() - 0.5) * h.WorldRect.Width * 0.6f, 0);
                Entity.Spawner?.AddItemToSpawnQueue(prefab, pos);
                Events++;
            }
        }
        public override void Tick(float dt) { }
    }

    sealed class Walk : Scenario
    {
        readonly float speed;
        List<Vector2> path;
        int leg;
        Vector2 pos;
        public Walk(float speed) => this.speed = speed * 100f;
        public override string Name => "walk";
        public override void Start()
        {
            path = hulls.Where(h => h.WorldRect.Height > 150).Select(Floor).ToList();
            pos = path.Count > 0 ? path[0] : Vector2.Zero;
        }
        public override void Tick(float dt)
        {
            Character walker = null;
#if CLIENT
            walker = Character.Controlled;
#endif
            if (walker == null || path.Count < 2) { return; }
            Vector2 target = path[(leg + 1) % path.Count];
            Vector2 d = target - pos;
            float step = speed * dt;
            if (d.Length() <= step) { pos = target; leg++; Events++; }
            else { pos += Vector2.Normalize(d) * step; }
            walker.TeleportTo(pos);
        }
    }
}
