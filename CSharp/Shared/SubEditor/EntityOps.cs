using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// The only place where editor operations touch Barotrauma entities. Server and client run the same code
/// (the server for the authoritative apply, clients for the broadcast), so both sides cannot drift apart.
/// Every apply is absolute (sets a value, never adds a delta) and therefore idempotent: an echo of an
/// operation the client already applied optimistically is harmless.
/// </summary>
public static class EntityOps
{
    public const string SpawnRoot = "Entities";

    public static MapEntity Find(ushort id) => Entity.FindEntityByID(id) as MapEntity;

    public static Rectangle LocalRect(Rectangle rect, Submarine submarine)
    {
        if (submarine != null) { rect.Location -= submarine.HiddenSubPosition.ToPoint(); }
        return rect;
    }

    internal static void SetLocalRect(MapEntity entity, Rectangle rect)
    {
        Submarine previous = entity.Submarine;
        if (entity.Submarine != null) { rect.Location += entity.Submarine.HiddenSubPosition.ToPoint(); }
        entity.Move(new Vector2(rect.X - entity.Rect.X, rect.Y - entity.Rect.Y));
        entity.Rect = rect;
        if (entity is Item item) { NormalizeFrame(item, previous); }
        LiveGeometry.Touch(entity);
    }

    private static void NormalizeFrame(Item item, Submarine previous)
    {
        if (item.body == null || previous == item.Submarine) { return; }
        Vector2 delta = (previous?.Position ?? Vector2.Zero) - (item.Submarine?.Position ?? Vector2.Zero);
        item.body.Submarine = item.Submarine;
        item.SetTransform(item.body.SimPosition + FarseerPhysics.ConvertUnits.ToSimUnits(delta), item.body.Rotation, findNewHull: false);
    }

    internal static bool SetFrame(MapEntity entity, ushort id)
    {
        Submarine owner = SubRegistry.Find(id);
        if (owner == null && id != SubRegistry.WorldId) { return false; }
        Submarine previous = entity.Submarine;
        if (previous == owner) { return true; }
        LiveGeometry.Touch(entity);
        entity.Submarine = owner;
        if (entity is Item item && item.body != null) { NormalizeFrame(item, previous); }
        else { entity.Move((previous?.Position ?? Vector2.Zero) - (owner?.Position ?? Vector2.Zero)); }
        LiveGeometry.Touch(entity);
        return true;
    }

    public static bool AllExist(ReadOnlySpan<ushort> ids)
    {
        for (int i = 0; i < ids.Length; i++) { if (Find(ids[i]) == null) { return false; } }
        return true;
    }

    // ---- Transform -------------------------------------------------------------------------------------------

    public static bool CaptureTransform(ReadOnlySpan<ushort> ids, Span<TransformRecord> dest)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity e = Find(ids[i]);
            if (e == null) { return false; }
            Rectangle r = LocalRect(e.Rect, e.Submarine);
            dest[i] = new TransformRecord
            {
                Fields = TransformFields.Position | TransformFields.Size | TransformFields.Rotation | TransformFields.Scale | TransformFields.Flags | TransformFields.SpriteDepth | TransformFields.Frame,
                X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Rotation = e.RotationRad, Scale = e.Scale,
                Flags = (byte)((e.FlippedX ? 1 : 0) | (e.FlippedY ? 2 : 0)), SpriteDepth = e.SpriteDepth, SubmarineId = SubRegistry.WireId(e.Submarine)
            };
        }
        return true;
    }

    public static bool ApplyTransform(ReadOnlySpan<ushort> ids, ReadOnlySpan<TransformRecord> records)
    {
        if (ids.Length != records.Length) { return false; }
        for (int i = 0; i < ids.Length; i++)
        {
            if (Find(ids[i]) == null || (records[i].Fields & TransformFields.Frame) != 0 && records[i].SubmarineId != SubRegistry.WorldId && SubRegistry.Find(records[i].SubmarineId) == null) { return false; }
        }
        using var geometry = LiveGeometry.Enter();
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity e = Find(ids[i]);
            if (e == null) { continue; }
            TransformRecord t = records[i];
            if ((t.Fields & TransformFields.Frame) != 0 && !SetFrame(e, t.SubmarineId)) { throw new InvalidOperationException("Transform submarine is unavailable"); }
            Rectangle r = LocalRect(e.Rect, e.Submarine);
            if ((t.Fields & TransformFields.Position) != 0) { r.X = (int)t.X; r.Y = (int)t.Y; }
            if ((t.Fields & TransformFields.Size) != 0) { r.Width = t.Width; r.Height = t.Height; }
            if ((t.Fields & TransformFields.Scale) != 0) { e.Scale = t.Scale; }
            if ((t.Fields & TransformFields.Rotation) != 0)
            {
                if (e is Item item) { item.Rotation = MathHelper.ToDegrees(t.Rotation); }
                else if (e is Structure structure) { structure.Rotation = MathHelper.ToDegrees(t.Rotation); }
                else { e.RotationRad = t.Rotation; }
            }
            if ((t.Fields & TransformFields.Flags) != 0)
            {
                if (e.FlippedX != ((t.Flags & 1) != 0)) { e.FlipX(false); }
                if (e.FlippedY != ((t.Flags & 2) != 0)) { e.FlipY(false); }
            }
            if ((t.Fields & TransformFields.SpriteDepth) != 0) { e.SpriteDepth = t.SpriteDepth; }
            SetLocalRect(e, r);
        }
        return true;
    }

    // ---- Properties ------------------------------------------------------------------------------------------

    private static ISerializableEntity Target(MapEntity e, short component)
    {
        if (component < 0) { return e as ISerializableEntity; }
        return e is Item item && component < item.Components.Count ? item.Components[component] : null;
    }

    public static string Stringify(object value)
    {
        switch (value)
        {
            case null: return "";
            case float f: return f.ToString("G", CultureInfo.InvariantCulture);
            case Point p: return XMLExtensions.PointToString(p);
            case Vector2 v2: return XMLExtensions.Vector2ToString(v2);
            case Vector3 v3: return XMLExtensions.Vector3ToString(v3);
            case Vector4 v4: return XMLExtensions.Vector4ToString(v4);
            case Color c: return XMLExtensions.ToStringHex(c);
            case Rectangle r: return XMLExtensions.RectToString(r);
            case string[] sa: return string.Join(';', sa);
            case Identifier[] ia: return string.Join(';', ia);
            default: return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    public static bool CaptureProperty(ReadOnlySpan<ushort> ids, ReadOnlySpan<short> components, string name, string[] values)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity e = Find(ids[i]);
            ISerializableEntity target = e == null ? null : Target(e, components[i]);
            if (target?.SerializableProperties == null || !target.SerializableProperties.TryGetValue(name.ToIdentifier(), out SerializableProperty p)) { return false; }
            values[i] = Stringify(p.GetValue(target));
        }
        return true;
    }

    public static bool ApplyProperty(ReadOnlySpan<ushort> ids, ReadOnlySpan<short> components, string name, string[] values)
    {
        if (ids.Length != components.Length || ids.Length != values.Length) { return false; }
        using var geometry = LiveGeometry.Enter();
        bool applied = true;
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity e = Find(ids[i]);
            ISerializableEntity target = e == null ? null : Target(e, components[i]);
            if (target?.SerializableProperties == null || !target.SerializableProperties.TryGetValue(name.ToIdentifier(), out SerializableProperty p)) { return false; }
            bool changed = p.TrySetValue(target, values[i]);
            applied &= changed;
            if (changed && name is "Scale" or "Rotation" or "RotationRad" or "Rect" or "FlippedX" or "FlippedY") { LiveGeometry.Touch(e); }
        }
        return applied;
    }

    // ---- Spawn / Delete --------------------------------------------------------------------------------------

    /// <summary>Entities plus everything they contain, ascending by id, without duplicates.</summary>
    public static List<MapEntity> WithContents(ReadOnlySpan<ushort> ids)
    {
        List<MapEntity> entities = CollectWithContents(ids);
        entities.Sort(static (a, b) => a.ID.CompareTo(b.ID));
        return entities;
    }

    // Postorder follows ownership rather than numeric IDs: an inventory's
    // contents can have lower IDs than their container after paste/restore.
    private static List<MapEntity> CollectWithContents(ReadOnlySpan<ushort> ids)
    {
        var entities = new List<MapEntity>();
        var seen = new HashSet<ushort>();
        var pending = new Stack<(MapEntity Entity, bool Complete)>();
        for (int i = 0; i < ids.Length; i++)
        {
            pending.Push((Find(ids[i]), false));
            while (pending.TryPop(out var step))
            {
                MapEntity entity = step.Entity;
                if (step.Complete) { entities.Add(entity); continue; }
                if (entity == null || !seen.Add(entity.ID)) { continue; }
                pending.Push((entity, true));
                if (entity is not Item item) { continue; }
                foreach (ItemComponent component in item.Components)
                {
                    if (component is Door door) { pending.Push((door.LinkedGap, false)); }
                }
                foreach (Item inner in item.ContainedItems) { pending.Push((inner, false)); }
            }
        }
        return entities;
    }

    // An item sitting in a container is saved without any link to it (the link lives in the container's own element), so an item saved on its own
    // (placed from the catalog into a slot, taken out and deleted, a snapshot entry) would come back loose. Its place travels as three attributes
    // the engine's loader ignores and Load reads back.
    private const string PlaceContainer = "ppContainer", PlaceComponent = "ppComponent", PlaceSlot = "ppSlot";

    /// <summary>Saves one entity into <paramref name="root"/>; an item inside a container that is not part of the same save keeps its place.</summary>
    public static void SaveInto(XElement root, MapEntity entity)
    {
        XElement element = entity.Save(root);
        if (element != null) { element.SetAttributeValue("ID", entity.ID); }
        if (element != null) { element.SetAttributeValue("ppSubmarine", SubRegistry.WireId(entity.Submarine)); }
        if (element == null || entity is not Item item || item.ParentInventory?.Owner is not Item container) { return; }
        if (!CapturePlacement(item, out InventoryPlacement place) || place.ContainerId == 0) { return; }
        element.SetAttributeValue(PlaceContainer, place.ContainerId.ToString(CultureInfo.InvariantCulture));
        element.SetAttributeValue(PlaceComponent, place.ComponentIndex.ToString(CultureInfo.InvariantCulture));
        element.SetAttributeValue(PlaceSlot, place.Slot.ToString(CultureInfo.InvariantCulture));
    }

    public static byte[] SaveXml(IReadOnlyList<MapEntity> entities)
    {
        var root = new XElement(SpawnRoot);
        foreach (MapEntity e in entities) { SaveInto(root, e); }
        return Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
    }

    private static (int Index, InventoryPlacement Place)[] ReadPlacements(XElement root)
    {
        var found = new List<(int, InventoryPlacement)>();
        int index = 0;
        foreach (XElement el in root.Elements())
        {
            if (el.Attribute(PlaceContainer) != null || el.Attribute(PlaceComponent) != null || el.Attribute(PlaceSlot) != null)
            {
                if (!ushort.TryParse((string)el.Attribute(PlaceContainer), NumberStyles.None, CultureInfo.InvariantCulture, out ushort container) || container == 0 ||
                    !short.TryParse((string)el.Attribute(PlaceComponent), NumberStyles.None, CultureInfo.InvariantCulture, out short component) ||
                    !int.TryParse((string)el.Attribute(PlaceSlot), NumberStyles.None, CultureInfo.InvariantCulture, out int slot)) { return null; }
                found.Add((index, new InventoryPlacement(container, component, slot)));
            }
            index++;
        }
        return found.ToArray();
    }

    private static bool LoadPlacements(List<MapEntity> loaded, XElement root, EntityIdMap ids, bool exact)
    {
        var explicitPlaces = ReadPlacements(root);
        if (explicitPlaces == null) { return false; }
        var placements = new SortedDictionary<ushort, InventoryPlacement>();
        var members = new HashSet<ushort>(loaded.Select(entity => entity.ID));
        foreach (var (at, place) in explicitPlaces)
        {
            if (loaded[at] is not Item item) { return false; }
            placements.Add(item.ID, place with { ContainerId = ids.Resolve(place.ContainerId) });
        }
        foreach (MapEntity entity in loaded)
        {
            if (entity is not Item owner) { continue; }
            for (int component = 0; component < owner.Components.Count; component++)
            {
                if (owner.Components[component] is not ItemContainer container || container.itemIds == null) { continue; }
                for (int slot = 0; slot < container.itemIds.Length; slot++)
                {
                    if (container.itemIds[slot] == null) { continue; }
                    foreach (ushort contained in container.itemIds[slot])
                    {
                        if (contained == 0) { continue; }
                        if (!members.Contains(contained)) { if (!exact) { return false; } continue; }
                        var placement = new InventoryPlacement(owner.ID, (short)component, slot);
                        if (placements.TryGetValue(contained, out InventoryPlacement previous) && previous != placement) { return false; }
                        placements[contained] = placement;
                    }
                }
                container.itemIds = null;
            }
        }
        if (placements.Count == 0) { return true; }
        return InventoryOps.Apply(placements.Keys.ToArray(), placements.Values.ToArray());
    }

    private static List<MapEntity> Load(ReadOnlySpan<byte> utf8, Submarine submarine, int idOffset, bool exact)
    {
        using var geometry = LiveGeometry.Enter();
        XElement root;
        try { root = XElement.Parse(Encoding.UTF8.GetString(utf8)); }
        catch (System.Xml.XmlException ex) { ModLog.Error($"subeditor: Load XML parse error: {ex.Message}"); return null; }
        if (root.Name != SpawnRoot || submarine is { Removed: true } || (submarine ?? Submarine.MainSub) is not Submarine loadOwner)
        {
            ModLog.Error($"subeditor: Load invalid root or sub: root={root.Name}, subRemoved={submarine?.Removed}, mainSub={Submarine.MainSub != null}");
            return null;
        }
        using var world = WorldContext.Enter();
        int[] sourceIds = ElementIds(root);
        int elemCount = root.Elements().Count();
        if (sourceIds.Length != elemCount || !EntityIdMap.TryCreate(sourceIds, exact ? 0 : idOffset, out EntityIdMap ids))
        {
            ModLog.Error($"subeditor: Load ID mapping failed: sourceIds={sourceIds.Length}, elements={elemCount}, offset={idOffset}");
            return null;
        }
        for (int i = 0; i < ids.Count; i++)
        {
            if (Entity.FindEntityByID(ids.Destination(i)) != null)
            {
                ModLog.Error($"subeditor: Load ID {ids.Destination(i)} is already occupied");
                return null;
            }
        }
        List<MapEntity> loaded = null;
        bool loading = loadOwner.Loading, committed = false;
        loadOwner.Loading = true;
        try
        {
            loaded = ExactIds.Load(loadOwner, root, ids, exact ? 0 : idOffset);
            if (loaded.Count != sourceIds.Length) { return null; }
            var owners = new Submarine[loaded.Count];
            int ownerIndex = 0;
            foreach (XElement element in root.Elements())
            {
                int sourceOwner = element.GetAttributeInt("ppSubmarine", SubRegistry.WireId(submarine));
                if ((uint)sourceOwner > ushort.MaxValue) { return null; }
                ushort ownerId = (ushort)sourceOwner;
                Submarine owner = SubRegistry.Find(ownerId);
                if (owner == null && ownerId != SubRegistry.WorldId) { return null; }
                owners[ownerIndex++] = owner;
            }
            for (int i = 0; i < loaded.Count; i++)
            {
                MapEntity entity = loaded[i];
                if (entity.ID != ids.Resolve(sourceIds[i]) || entity.Removed) { return null; }
                entity.Submarine = owners[i];
                if (entity is Item { body: not null } item) { item.body.Submarine = owners[i]; }
                if (owners[i] != null) { entity.Move(owners[i].HiddenSubPosition, ignoreContacts: true); }
            }
            foreach (MapEntity entity in loaded) { LiveGeometry.Touch(entity); }
            if (!LoadPlacements(loaded, root, ids, exact)) { return null; }
            MapEntity.MapLoaded(loaded, updateHulls: true);
            for (int i = 0; i < loaded.Count; i++)
            {
                if (loaded[i] is Item item) { NormalizeFrame(item, owners[i]); item.SetContainedItemPositions(); }
            }
            committed = true;
            return loaded;
        }
        finally
        {
            if (!committed)
            {
                for (int i = ids.Count - 1; i >= 0; i--)
                {
                    if (Find(ids.Destination(i)) is MapEntity { Removed: false } entity) { LiveGeometry.Touch(entity); entity.Remove(); }
                }
            }
            loadOwner.Loading = loading;
        }
    }

    /// <summary>
    /// Creates entities from XML of unknown origin (a client's request). The engine numbers them contiguously from
    /// <paramref name="firstId"/>, a window the caller found free (see <see cref="TryFindIdOffset"/>). Safe to call with hostile input.
    /// </summary>
    public static List<MapEntity> LoadAt(ReadOnlySpan<byte> utf8, Submarine submarine, int firstId) => Load(utf8, submarine, firstId, exact: false);

    /// <summary>Re-creates entities under exactly the ids in the XML. The caller has made sure those ids are free.</summary>
    public static List<MapEntity> LoadExact(ReadOnlySpan<byte> utf8, Submarine submarine) => Load(utf8, submarine, 0, exact: true);

    public static void Delete(ReadOnlySpan<ushort> ids)
    {
        using var geometry = LiveGeometry.Enter();
        // Remove children before owners so native ItemContainer.Remove does not
        // drop them into the world and create unrelated physics/network events.
        foreach (MapEntity e in CollectWithContents(ids))
        {
            if (!e.Removed)
            {
                LiveGeometry.Touch(e);
                e.Remove();
            }
        }
    }

    /// <summary>
    /// Removes exactly the authoritative set during snapshot reconciliation.
    /// Local containment may lag behind the server: surviving children must not
    /// be deleted merely because their previous container is gone or replaced.
    /// </summary>
    public static void DeleteExact(ReadOnlySpan<ushort> ids)
    {
        using var geometry = LiveGeometry.Enter();
        using var world = WorldContext.Enter();
        var selected = new HashSet<ushort>();
        foreach (ushort id in ids) { selected.Add(id); }
        List<MapEntity> closure = CollectWithContents(ids);
        foreach (MapEntity entity in closure)
        {
            if (entity is not Item item || selected.Contains(item.ID) ||
                item.ParentInventory?.Owner is not Item owner || !selected.Contains(owner.ID)) { continue; }
            // RemoveItem updates both inventory slots and ParentInventory without
            // invoking Drop effects or creating an unrelated native network event.
            item.ParentInventory.RemoveItem(item);
        }
        foreach (MapEntity entity in closure)
        {
            if (!selected.Contains(entity.ID) || entity.Removed) { continue; }
            LiveGeometry.Touch(entity);
            entity.Remove();
        }
    }

    /// <summary>
    /// Finds the first id of a window for a block of <paramref name="count"/> entities, to be passed to the loader as the id offset
    /// (it renumbers the block contiguously from there). The window comes from the top of the editor's id zone (see <see cref="IdWindow"/>),
    /// so it cannot collide with entities a peer creates on its own.
    /// </summary>
    public static bool TryFindIdOffset(int count, out int offset)
    {
        if (IdWindow.TryFindFromTop(id => Entity.FindEntityByID((ushort)id) != null, SubRegistry.EditorIdLimit, count, out offset))
        {
            return true;
        }
        int free = Entity.FindFreeIdBlock(count);
        if (free > 0)
        {
            offset = free;
            return true;
        }
        offset = 0;
        return false;
    }

    /// <summary>The ids of the direct children of a spawn root or saved submarine element that carry one, in document order. Null when the XML is not parseable.</summary>
    public static int[] ElementIds(ReadOnlySpan<byte> utf8)
    {
        try { return ElementIds(XElement.Parse(Encoding.UTF8.GetString(utf8))); }
        catch (System.Xml.XmlException) { return null; }
    }

    public static int[] ElementIds(XElement root) =>
        root.Elements()
            .Select(el => int.TryParse((string)el.Attribute("ID"), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : 0)
            .Where(id => id >= 1)
            .ToArray();

    public static XElement Blueprint(XElement source)
    {
        if (source == null) { return null; }
        var root = new XElement(source);
        int max = 0;
        foreach (XElement element in root.Elements()) { max = Math.Max(max, element.GetAttributeInt("ID", 0)); }
        foreach (XElement element in root.Elements())
        {
            if (!element.Name.LocalName.Equals(nameof(LinkedSubmarine), StringComparison.OrdinalIgnoreCase) || element.GetAttributeInt("ID", 0) > 0) { continue; }
            if (++max >= ushort.MaxValue) { return null; }
            element.SetAttributeValue("ID", max);
        }
        return root;
    }

    // ---- Entity state (tool edits, snapshot of pre-existing entities) -----------------------------------------

    private const string StateName = "S";

    /// <summary>
    /// Everything the editor can change on an entity, saved regardless of defaults so that restoring it also resets
    /// values back to default: rect, serializable properties, per-component properties, wire nodes.
    /// </summary>
    public static XElement CaptureState(MapEntity entity)
    {
        var el = new XElement(StateName);
        Rectangle r = LocalRect(entity.Rect, entity.Submarine);
        el.SetAttributeValue("rect", string.Join(",", r.X, r.Y, r.Width, r.Height));
        el.SetAttributeValue("ppSubmarine", SubRegistry.WireId(entity.Submarine));
        var links = new XElement("Links");
        foreach (MapEntity link in entity.linkedTo) { if (!link.Removed) { links.Add(new XElement("E", new XAttribute("id", link.ID))); } }
        el.Add(links);
        if (entity is ISerializableEntity se) { SerializableProperty.SerializeProperties(se, el, saveIfDefault: true); }
        if (entity is Item item)
        {
            if (CapturePlacement(item, out InventoryPlacement placement))
            {
                el.SetAttributeValue(PlaceContainer, placement.ContainerId);
                el.SetAttributeValue(PlaceComponent, placement.ComponentIndex);
                el.SetAttributeValue(PlaceSlot, placement.Slot);
            }
            for (int i = 0; i < item.Components.Count; i++)
            {
                ItemComponent ic = item.Components[i];
                var child = new XElement("C");
                child.SetAttributeValue("i", i);
                SerializableProperty.SerializeProperties(ic, child, saveIfDefault: true);
                if (ic is Wire && CaptureWire(item.ID, out WireState wireState))
                {
                    var writer = new ByteWriter(128);
                    try { WirePayload.Write(ref writer, new[] { wireState }); child.Add(new XElement("Wire", Convert.ToBase64String(writer.Written))); }
                    finally { writer.Dispose(); }
                }
                el.Add(child);
            }
        }
        return el;
    }

    public static byte[] CaptureStateBytes(MapEntity entity) =>
        Encoding.UTF8.GetBytes(CaptureState(entity).ToString(SaveOptions.DisableFormatting));

    public static bool CaptureStates(ReadOnlySpan<ushort> ids, byte[][] dest)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity e = Find(ids[i]);
            if (e == null) { return false; }
            dest[i] = CaptureStateBytes(e);
        }
        return true;
    }

    public static bool RestoreState(MapEntity entity, ReadOnlySpan<byte> utf8)
    {
        XElement state;
        try { state = XElement.Parse(Encoding.UTF8.GetString(utf8)); }
        catch (System.Xml.XmlException) { return false; }
        RestoreState(entity, state);
        return true;
    }

    public static bool RestoreStates(ReadOnlySpan<ushort> ids, byte[][] blobs) => RestoreStates(ids, blobs, out _);

    public static bool RestoreStates(ReadOnlySpan<ushort> ids, byte[][] blobs, out string failure)
    {
        failure = null;
        using var geometry = LiveGeometry.Enter();
        using var world = WorldContext.Enter();
        if (ids.Length != blobs.Length || !OpFrame.AreStrictlyAscending(ids)) { failure = "State IDs or payload count are invalid"; return false; }
        var states = new XElement[ids.Length];
        var before = new XElement[ids.Length];
        try
        {
            for (int i = 0; i < ids.Length; i++)
            {
                if (Find(ids[i]) is not MapEntity entity) { failure = $"Missing entity {ids[i]} before state restoration"; return false; }
                states[i] = XElement.Parse(Encoding.UTF8.GetString(blobs[i]));
                before[i] = CaptureState(entity);
            }
        }
        catch (System.Xml.XmlException error) { failure = "Invalid state XML: " + error.Message; return false; }
        bool committed = false;
        try { committed = RestoreStateSet(ids, states, out failure); return committed; }
        finally
        {
            if (!committed && !RestoreStateSet(ids, before, out string rollbackFailure)) { ModLog.Error("subeditor: entity state rollback failed: " + rollbackFailure); }
        }
    }

    private static bool RestoreStateSet(ReadOnlySpan<ushort> ids, XElement[] states, out string failure)
    {
        failure = null;
        var itemIds = new List<ushort>();
        var placements = new List<InventoryPlacement>();
        var wires = new List<WireState>();
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity entity = Find(ids[i]);
            RestoreState(entity, states[i], restoreRelations: false);
            if (entity is not Item item) { continue; }
            XElement state = states[i];
            if (state.Attribute(PlaceContainer) != null)
            {
                if (!ushort.TryParse((string)state.Attribute(PlaceContainer), out ushort container) ||
                    !short.TryParse((string)state.Attribute(PlaceComponent), out short component) ||
                    !int.TryParse((string)state.Attribute(PlaceSlot), out int slot)) { failure = $"Invalid inventory state for entity {item.ID}"; return false; }
                Rectangle rect = LocalRect(item.Rect, item.Submarine);
                itemIds.Add(item.ID);
                placements.Add(container == 0 ? new InventoryPlacement(0, -1, -1, SubRegistry.WireId(item.Submarine), rect.X, rect.Y, item.RotationRad)
                    : new InventoryPlacement(container, component, slot));
            }
            foreach (XElement child in state.Elements("C"))
            {
                if (child.Element("Wire") is not XElement wireData) { continue; }
                byte[] data;
                try { data = Convert.FromBase64String(wireData.Value); }
                catch (FormatException) { failure = $"Invalid wire encoding for entity {item.ID}"; return false; }
                if (!WirePayload.TryRead(data, out WireState[] decoded) || decoded.Length != 1 || decoded[0].WireId != item.ID) { failure = $"Invalid wire state for entity {item.ID}"; return false; }
                wires.Add(decoded[0]);
            }
        }
        if (itemIds.Count != 0 && !InventoryOps.Apply(itemIds.ToArray(), placements.ToArray()))
        { failure = $"Inventory restoration rejected ({itemIds.Count} items; first ID {itemIds[0]})"; return false; }
        if (wires.Count != 0 && !ApplyWires(wires.ToArray()))
        { failure = $"Wire restoration rejected ({wires.Count} wires; first ID {wires[0].WireId})"; return false; }
        return true;
    }

    public static void RestoreState(MapEntity entity, XElement state, bool restoreRelations = true)
    {
        using var geometry = LiveGeometry.Enter();
        if (ushort.TryParse((string)state.Attribute("ppSubmarine"), out ushort submarine) && !SetFrame(entity, submarine))
        { ModLog.Error("subeditor: state submarine is unavailable"); return; }
        if (entity is ISerializableEntity se) { SerializableProperty.DeserializeProperties(se, state); }
        if (entity is Item item)
        {
            foreach (XElement child in state.Elements("C"))
            {
                if (!int.TryParse((string)child.Attribute("i"), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || (uint)index >= (uint)item.Components.Count) { continue; }
                ItemComponent ic = item.Components[index];
                SerializableProperty.DeserializeProperties(ic, child);
                if (restoreRelations && ic is Wire && child.Element("Wire") is XElement wireData)
                {
                    if (!WirePayload.TryRead(Convert.FromBase64String(wireData.Value), out WireState[] wires) || wires.Length != 1 || wires[0].WireId != item.ID || !ApplyWire(wires[0]))
                    { ModLog.Error("subeditor: wire state could not be restored"); }
                }
            }
            if (restoreRelations && ushort.TryParse((string)state.Attribute(PlaceContainer), out ushort container) &&
                short.TryParse((string)state.Attribute(PlaceComponent), out short component) &&
                int.TryParse((string)state.Attribute(PlaceSlot), out int slot))
            {
                if (container == 0) { item.Drop(null, createNetworkEvent: false, setTransform: false); }
                else if (!ApplyPlacement(item, new InventoryPlacement(container, component, slot))) { ModLog.Error("subeditor: inventory state could not be restored"); }
            }
        }
        if (state.Element("Links") is XElement links)
        {
            entity.linkedTo.Clear();
            foreach (XElement link in links.Elements("E"))
            {
                if (ushort.TryParse((string)link.Attribute("id"), out ushort id) && Find(id) is MapEntity target && !target.Removed) { entity.linkedTo.Add(target); }
            }
        }
        string rect = (string)state.Attribute("rect");
        if (rect == null) { return; }
        string[] p = rect.Split(',');
        if (p.Length == 4 && int.TryParse(p[0], out int rx) && int.TryParse(p[1], out int ry) && int.TryParse(p[2], out int rw) && int.TryParse(p[3], out int rh))
        {
            SetLocalRect(entity, new Rectangle(rx, ry, rw, rh));
        }
    }

    // ---- Links -------------------------------------------------------------------------------------------------

    public static bool IsLinked(ushort a, ushort b) => Find(a) is MapEntity ea && Find(b) is MapEntity eb && ea.linkedTo.Contains(eb);

    /// <summary>Makes the link exist (or not) on both sides. Absolute, so repeating it is harmless.</summary>
    public static void SetLink(ushort a, ushort b, bool linked)
    {
        MapEntity ea = Find(a), eb = Find(b);
        if (ea == null || eb == null) { return; }
        if (linked)
        {
            if (!ea.linkedTo.Contains(eb)) { ea.linkedTo.Add(eb); }
            if (!eb.linkedTo.Contains(ea)) { eb.linkedTo.Add(ea); }
        }
        else { ea.linkedTo.Remove(eb); eb.linkedTo.Remove(ea); }
    }

    // ---- Flip --------------------------------------------------------------------------------------------------

    public static bool CaptureFlip(ReadOnlySpan<ushort> ids, Span<FlipRecord> dest)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity e = Find(ids[i]);
            if (e == null) { return false; }
            Rectangle r = LocalRect(e.Rect, e.Submarine);
            dest[i] = new FlipRecord(e.FlippedX, e.FlippedY, r.X, r.Y, r.Width, r.Height, SubRegistry.WireId(e.Submarine));
        }
        return true;
    }

    /// <summary>
    /// Brings each entity to the recorded orientation: the engine's own flip runs only for an axis that differs (it also mirrors rotation,
    /// sprite effects and component state), then the rectangle is set absolutely like in <see cref="ApplyTransform"/>.
    /// </summary>
    public static bool ApplyFlip(ReadOnlySpan<ushort> ids, ReadOnlySpan<FlipRecord> records)
    {
        if (ids.Length != records.Length) { return false; }
        for (int i = 0; i < ids.Length; i++)
        {
            if (Find(ids[i]) == null || records[i].SubmarineId != SubRegistry.WorldId && SubRegistry.Find(records[i].SubmarineId) == null) { return false; }
        }
        using var geometry = LiveGeometry.Enter();
        for (int i = 0; i < ids.Length; i++)
        {
            MapEntity e = Find(ids[i]);
            if (e == null) { continue; }
            FlipRecord f = records[i];
            if (!SetFrame(e, f.SubmarineId)) { ModLog.Error("subeditor: flip submarine is unavailable"); continue; }
            if (e.FlippedX != f.FlippedX) { e.FlipX(false); }
            if (e.FlippedY != f.FlippedY) { e.FlipY(false); }
            SetLocalRect(e, new Rectangle(f.X, f.Y, f.Width, f.Height));
        }
        return true;
    }

    // ---- Inventory ---------------------------------------------------------------------------------------------

    public static bool CapturePlacement(Item item, out InventoryPlacement placement) => InventoryOps.Capture(item, out placement);

    public static bool ApplyPlacement(Item item, InventoryPlacement p)
    {
        return item != null && InventoryOps.Apply(new[] { item.ID }, new[] { p });
    }

    // ---- Wires -------------------------------------------------------------------------------------------------

    private static WireEnd EndOf(Connection c)
    {
        if (c == null) { return default; }
        int idx = c.ConnectionPanel.Connections.IndexOf(c);
        return idx < 0 ? default : new WireEnd(c.Item.ID, (short)idx);
    }

    private static ConnectionPanel PanelOf(WireEnd end) => (Find(end.ItemId) as Item)?.GetComponent<ConnectionPanel>();

    private static Connection ConnectionOf(WireEnd e)
    {
        if (e.ItemId == 0 || Find(e.ItemId) is not Item item) { return null; }
        ConnectionPanel panel = item.GetComponent<ConnectionPanel>();
        return panel != null && e.ConnectionIndex >= 0 && e.ConnectionIndex < panel.Connections.Count ? panel.Connections[e.ConnectionIndex] : null;
    }

    public static bool CaptureWire(ushort wireId, out WireState state)
    {
        state = default;
        Wire wire = (Find(wireId) as Item)?.GetComponent<Wire>();
        if (wire == null) { return false; }
        WireEnd end0 = EndOf(wire.Connections[0]), end1 = EndOf(wire.Connections[1]);
        if (end0.ItemId == 0 || end1.ItemId == 0)
        {
            for (int i = 0; i < Item.ItemList.Count; i++)
            {
                Item item = Item.ItemList[i]; if (item == null || item.Removed) { continue; } ConnectionPanel panel = item.GetComponent<ConnectionPanel>();
                if (panel == null || !panel.DisconnectedWires.Contains(wire)) { continue; }
                if (end0.ItemId == 0) { end0 = new WireEnd(item.ID, -1); }
                else if (end1.ItemId == 0) { end1 = new WireEnd(item.ID, -1); }
            }
        }
        List<Vector2> points = wire.GetNodes();
        var nodes = new (float, float)[points.Count];
        for (int i = 0; i < nodes.Length; i++) { nodes[i] = (points[i].X, points[i].Y); }
        state = new WireState(wireId, end0, end1, nodes);
        return true;
    }

    public static bool CanApplyWire(WireState state, bool creating = false) => ValidateWire(state, creating, capacity: true);

    private static bool ValidateWire(WireState state, bool creating, bool capacity)
    {
        Wire wire = creating ? null : (Find(state.WireId) as Item)?.GetComponent<Wire>();
        if (!creating && wire == null || state.Nodes == null || state.Nodes.Length > WirePayload.MaxNodes) { return false; }
        if (state.End0.ItemId != 0 && state.End0 == state.End1) { return false; }
        Submarine owner = null;
        bool hasOwner = false;
        for (int i = 0; i < 2; i++)
        {
            WireEnd end = i == 0 ? state.End0 : state.End1;
            if (end.ItemId == 0) { continue; }
            ConnectionPanel panel = PanelOf(end);
            if (panel == null || end.ConnectionIndex < -1 || end.ConnectionIndex >= panel.Connections.Count) { return false; }
            if (hasOwner && owner != panel.Item.Submarine) { return false; }
            owner = panel.Item.Submarine; hasOwner = true;
            if (end.ConnectionIndex < 0) { continue; }
            Connection target = panel.Connections[end.ConnectionIndex];
            if (capacity && !target.Wires.Contains(wire) && !target.WireSlotsAvailable()) { return false; }
        }
        for (int i = 0; i < state.Nodes.Length; i++) { if (!float.IsFinite(state.Nodes[i].X) || !float.IsFinite(state.Nodes[i].Y)) { return false; } }
        return true;
    }

    public static bool CanApplyWires(ReadOnlySpan<WireState> states)
    {
        var counts = new Dictionary<Connection, int>();
        ushort previous = 0;
        for (int i = 0; i < states.Length; i++)
        {
            WireState state = states[i];
            if (state.WireId <= previous || !ValidateWire(state, creating: false, capacity: false)) { return false; }
            previous = state.WireId;
            Wire wire = ((Item)Find(state.WireId)).GetComponent<Wire>();
            for (int end = 0; end < 2; end++)
            {
                Connection have = wire.Connections[end], want = ConnectionOf(end == 0 ? state.End0 : state.End1);
                if (have == want) { continue; }
                if (have != null) { counts[have] = (counts.TryGetValue(have, out int n) ? n : have.Wires.Count) - 1; }
                if (want != null) { counts[want] = (counts.TryGetValue(want, out int n) ? n : want.Wires.Count) + 1; }
            }
        }
        foreach (var pair in counts) { if (pair.Value < 0 || pair.Value > pair.Key.MaxWires) { return false; } }
        return true;
    }

    public static bool ApplyWires(ReadOnlySpan<WireState> states)
    {
        if (!CanApplyWires(states)) { return false; }
        for (int i = 0; i < states.Length; i++)
        {
            WireState state = states[i];
            Wire wire = ((Item)Find(state.WireId)).GetComponent<Wire>();
            for (int end = 0; end < 2; end++)
            {
                Connection have = wire.Connections[end], want = ConnectionOf(end == 0 ? state.End0 : state.End1);
                if (have == null || have == want) { continue; }
                have.DisconnectWire(wire); wire.RemoveConnection(have);
            }
        }
        for (int i = 0; i < states.Length; i++) { if (!ApplyWire(states[i])) { return false; } }
        return true;
    }

    public static bool ApplyWire(WireState s)
    {
        Wire wire = (Find(s.WireId) as Item)?.GetComponent<Wire>();
        if (wire == null || !CanApplyWire(s) || !CaptureWire(s.WireId, out WireState before)) { return false; }
        WireEnd anchor = s.End0.ItemId != 0 ? s.End0 : s.End1;
        if (anchor.ItemId != 0 && !SetFrame(wire.Item, SubRegistry.WireId(PanelOf(anchor).Item.Submarine))) { return false; }
        for (int i = 0; i < 2; i++)
        {
            WireEnd old = i == 0 ? before.End0 : before.End1;
            if (old.ItemId != 0 && old.ConnectionIndex == -1) { PanelOf(old)?.DisconnectedWires.Remove(wire); }
        }
        for (int i = 0; i < 2; i++)
        {
            Connection have = wire.Connections[i], target = ConnectionOf(i == 0 ? s.End0 : s.End1);
            if (have == target) { continue; }
            if (have != null) { have.DisconnectWire(wire); wire.RemoveConnection(have); }
        }
        for (int i = 0; i < 2; i++)
        {
            WireEnd end = i == 0 ? s.End0 : s.End1;
            Connection target = ConnectionOf(end);
            if (target == null)
            {
                if (end.ItemId != 0 && end.ConnectionIndex == -1 && PanelOf(end) is ConnectionPanel panel)
                {
                    if (!panel.DisconnectedWires.Contains(wire)) { panel.DisconnectedWires.Add(wire); }
                    panel.IsActive = true;
                }
                continue;
            }
            if (wire.Connections[i] != target && !wire.Connect(target, i, addNode: false)) { return false; }
            target.ConnectWire(wire);
        }
        var nodes = new Vector2[s.Nodes.Length];
        for (int i = 0; i < nodes.Length; i++) { nodes[i] = new Vector2(s.Nodes[i].X, s.Nodes[i].Y); }
        wire.SetNodes(nodes);
        return true;
    }

    /// <summary>Wires plus every item at an old or new end: all of them are protected by the conflict check.</summary>
    public static ushort[] WireTouchedIds(IEnumerable<WireState> targets, IEnumerable<WireState> current)
    {
        var set = new SortedSet<ushort>();
        foreach (WireState s in targets.Concat(current))
        {
            set.Add(s.WireId);
            if (s.End0.ItemId != 0) { set.Add(s.End0.ItemId); }
            if (s.End1.ItemId != 0) { set.Add(s.End1.ItemId); }
        }
        return set.ToArray();
    }
}
