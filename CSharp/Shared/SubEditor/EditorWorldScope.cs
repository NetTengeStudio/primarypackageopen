using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.SubEditor;

public sealed class EditorWorldScope : IDisposable
{
    private sealed class ScopeView<T> where T : MapEntity
    {
        private readonly List<T> list;
        private readonly T[] previous;

        public ScopeView(List<T> list, Submarine submarine)
        {
            this.list = list;
            previous = list.ToArray();
            for (int i = list.Count - 1; i >= 0; i--) { if (list[i].Submarine != submarine) { list.RemoveAt(i); } }
        }

        public void Restore(bool ordered = false)
        {
            var known = new HashSet<T>(previous);
            var added = new List<T>();
            foreach (T entity in list) { if (!entity.Removed && !known.Contains(entity)) { added.Add(entity); } }
            list.Clear();
            foreach (T entity in previous) { if (!entity.Removed) { list.Add(entity); } }
            foreach (T entity in added)
            {
                if (ordered) { entity.InsertToList(); }
                else { list.Add(entity); }
            }
        }
    }

    private readonly Submarine submarine;
    private readonly Vector2 position;
    private readonly ScopeView<MapEntity> entities;
    private readonly ScopeView<Hull> hulls;
    private readonly ScopeView<Gap> gaps;
    private readonly ScopeView<Item> items;
    private readonly ScopeView<Structure> walls;
    private readonly ScopeView<WayPoint> waypoints;
    private bool disposed;

    public EditorWorldScope(Submarine submarine)
    {
        this.submarine = submarine ?? throw new ArgumentNullException(nameof(submarine));
        position = submarine.SubBody.Position;
        entities = new(MapEntity.MapEntityList, submarine);
        hulls = new(Hull.HullList, submarine);
        gaps = new(Gap.GapList, submarine);
        items = new(Item.ItemList, submarine);
        walls = new(Structure.WallList, submarine);
        waypoints = new(WayPoint.WayPointList, submarine);
        submarine.SubBody.SetPosition(submarine.HiddenSubPosition);
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        submarine.SubBody.SetPosition(position);
        hulls.Restore(); gaps.Restore(); items.Restore(); walls.Restore(); waypoints.Restore(); entities.Restore(ordered: true);
    }
}
