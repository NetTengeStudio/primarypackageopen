using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.SubEditor;

public static class LiveGeometry
{
    private static readonly HashSet<Submarine> dirty = new();
    private static int depth;
    private static long nextRebuild;

    public readonly struct Scope : IDisposable
    {
        public void Dispose()
        {
            if (--depth != 0) { return; }
            Flush(force: true);
        }
    }

    public static Scope Enter() { depth++; return default; }

    public static void Flush(bool force = true)
    {
        if (dirty.Count == 0) { return; }
        long now = Environment.TickCount64;
        if (!force && now < nextRebuild) { return; }
        nextRebuild = now + 66;
        try
        {
            while (dirty.Count > 0)
            {
                Submarine sub;
                using (var cursor = dirty.GetEnumerator()) { cursor.MoveNext(); sub = cursor.Current; }
                dirty.Remove(sub);
                if (sub != null && !sub.Removed) { Rebuild(sub); }
            }
        }
        finally { dirty.Clear(); }
    }

    public static void Touch(MapEntity entity)
    {
        if (entity?.Submarine?.SubBody == null || !Contributes(entity)) { return; }
        dirty.Add(entity.Submarine);
        if (depth == 0)
        {
            Flush(force: false);
        }
    }

    private static bool Contributes(MapEntity entity) => entity is Hull or Structure || entity is Item item && (item.StaticBodyConfig != null || item.GetComponent<Door>() != null);

    public static void Rebuild(Submarine sub)
    {
        using var world = WorldContext.Enter();
        SubmarineBody original = sub.SubBody;
        if (original?.Body == null || sub.Removed) { return; }
        Body body = original.Body.FarseerBody;
        Vector2 velocity = body.LinearVelocity;
        float angularVelocity = body.AngularVelocity;
        bool loading = sub.Loading;
        sub.Loading = true;
        var entities = new List<MapEntity>(); for (int i = 0; i < MapEntity.MapEntityList.Count; i++) { MapEntity e = MapEntity.MapEntityList[i]; if (e != null && !e.Removed && e.Submarine == sub && Contributes(e)) { entities.Add(e); } }
        var oldFixtures = new List<(Item Item, Fixture[] Fixtures, Fixture Door)>();
        Fixture[] previousFixtures = body.FixtureList.ToArray();
        var addedFixtures = new List<Fixture>();
        Rectangle borders = original.Borders, visibleBorders = original.VisibleBorders;
        var vertices = original.HullVertices;
        EntityGrid grid = sub.entityGrid, nextGrid = null;
        SubmarineBody replacement = null;
        int moved = 0;
        bool committed = false;
        try
        {
            foreach (MapEntity entity in entities)
            {
                if (entity is Item item)
                {
                    oldFixtures.Add((item, item.StaticFixtures.ToArray(), item.GetComponent<Door>()?.OutsideSubmarineFixture));
                    item.StaticFixtures.Clear();
                }
                entity.Move(-sub.HiddenSubPosition, ignoreContacts: true);
                moved++;
            }
            replacement = new SubmarineBody(sub, showErrorMessages: false);
            var copies = new Dictionary<Fixture, Fixture>();
            foreach (Fixture fixture in replacement.Body.FarseerBody.FixtureList)
            {
                Fixture copy = fixture.CloneOnto(body);
                addedFixtures.Add(copy);
                copies.Add(fixture, copy);
            }
            foreach (var entry in oldFixtures)
            {
                List<Fixture> fixtures = entry.Item.StaticFixtures;
                for (int i = 0; i < fixtures.Count; i++) { fixtures[i] = copies[fixtures[i]]; }
                if (entry.Item.GetComponent<Door>() is Door door && door.OutsideSubmarineFixture != null)
                { door.OutsideSubmarineFixture = copies[door.OutsideSubmarineFixture]; }
            }
            original.Borders = replacement.Borders;
            original.VisibleBorders = replacement.VisibleBorders;
            original.HullVertices = replacement.HullVertices;
            nextGrid = Hull.GenerateEntityGrid(sub);
            foreach (Fixture fixture in previousFixtures) { body.Remove(fixture); }
            if (grid != null) { Hull.EntityGrids.Remove(grid); }
            sub.entityGrid = nextGrid;
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                foreach (Fixture fixture in addedFixtures) { body.Remove(fixture); }
                original.Borders = borders;
                original.VisibleBorders = visibleBorders;
                original.HullVertices = vertices;
                if (nextGrid != null) { Hull.EntityGrids.Remove(nextGrid); }
                sub.entityGrid = grid;
                foreach (var entry in oldFixtures)
                {
                    entry.Item.StaticFixtures.Clear();
                    entry.Item.StaticFixtures.AddRange(entry.Fixtures);
                    if (entry.Item.GetComponent<Door>() is Door door) { door.OutsideSubmarineFixture = entry.Door; }
                }
            }
            replacement?.Body.Remove();
            for (int i = 0; i < moved; i++) { entities[i].Move(sub.HiddenSubPosition, ignoreContacts: true); }
            body.LinearVelocity = velocity;
            body.AngularVelocity = angularVelocity;
            sub.Loading = loading;
        }
        Item.UpdateHulls();
        Gap.UpdateHulls();
        dirty.Remove(sub);
    }
}
