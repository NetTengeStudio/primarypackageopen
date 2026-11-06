using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// Circuit box edits (see <see cref="CircuitOps"/>). The server applies the editor's request to its own box with the engine's internal
/// operations, so what a node, wire or label does is the engine's; what it adds is the part the engine hands to the entity spawner and to a
/// character at the box: backing items are created here under ids from the editor's id window and travel as ordinary Spawn and Inventory
/// steps of the same frame, followed by the engine's own resolved event.
/// </summary>
public sealed partial class SubEditorServer
{
    private static byte[] Pack<T>(CircuitBoxOpcode opcode, T data) where T : INetSerializableStruct
    {
        var message = new WriteOnlyMessage();
        message.WriteByte((byte)opcode);
        message.WriteNetSerializableStruct(data);
        return message.Buffer.AsSpan(0, message.LengthBytes).ToArray();
    }

    private Prepared PerformCircuit(byte actor, ulong baseRev, ushort[] ids, byte[] payload)
    {
        if (ids.Length != 1) { return Fail(SubmitStatus.Malformed); }
        if (!CircuitPayload.TryRead(payload, out byte index, out byte[] request)) { return Fail(SubmitStatus.Malformed); }
        if (EntityOps.Find(ids[0]) is not Item boxItem || index >= boxItem.Components.Count || boxItem.Components[index] is not CircuitBox box) { return Fail(SubmitStatus.Malformed); }
        int conflict = authority.Versions.FirstConflict(ids, actor, baseRev);
        if (conflict >= 0) { return Fail(SubmitStatus.Conflict, conflict); }

        var message = new ReadOnlyMessage(request, isCompressed: false, startPos: 0, byteLength: request.Length, sender: null);
        var opcode = (CircuitBoxOpcode)message.ReadByte();
        var checkpoint = new CircuitCheckpoint(box);
        bool complete = false;
        try
        {
            var work = new CircuitWork(boxItem);
            byte[] resolved;
            try
            {
                using (CircuitOps.Enter()) { resolved = Resolve(box, opcode, message, work); }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException or System.IO.EndOfStreamException)
            {
                ModLog.Once($"subeditor: circuit box {opcode} failed: {e.Message}");
                return Fail(SubmitStatus.Malformed);
            }
            if (resolved == null) { return Fail(SubmitStatus.Malformed); }

            var w = new ByteWriter(resolved.Length + 8);
            byte[] circuit;
            try { CircuitPayload.Write(ref w, index, resolved); circuit = w.Written.ToArray(); }
            finally { w.Dispose(); }

            var subs = new List<BatchPayload.Sub>(work.Before);
            subs.Add(new BatchPayload.Sub(OpKind.Circuit, new[] { boxItem.ID }, circuit));
            subs.AddRange(work.After);
            var batch = new ByteWriter(512);
            byte[] redo;
            try { BatchPayload.Write(ref batch, subs.ToArray()); redo = batch.Written.ToArray(); }
            finally { batch.Dispose(); }
            var operation = new Prepared { Kind = OpKind.Batch, UndoKind = OpKind.Batch, Ids = work.Touched.ToArray(), Redo = redo, Undo = Array.Empty<byte>(), Record = false, Rollback = checkpoint.Restore };
            complete = true;
            return operation;
        }
        finally { if (!complete) { checkpoint.Restore(); } }
    }

    private sealed class CircuitCheckpoint
    {
        private readonly CircuitBox box;
        private readonly XElement state;
        private readonly byte[] contents;
        private readonly ushort[] ids;
        private readonly Submarine owner;

        public CircuitCheckpoint(CircuitBox box)
        {
            this.box = box;
            owner = box.Item.Submarine;
            state = box.Save(new XElement("State"));
            List<MapEntity> entities = EntityOps.WithContents(box.Item.ContainedItems.Select(item => item.ID).OrderBy(id => id).ToArray());
            ids = entities.Select(entity => entity.ID).ToArray();
            contents = ids.Length == 0 ? null : EntityOps.SaveXml(entities);
        }

        public void Restore()
        {
            using var scope = CircuitOps.Enter();
            foreach (CircuitBoxWire wire in box.Wires)
            {
                if (wire.BackingWire.TryUnwrap(out Item item)) { item.GetComponent<Barotrauma.Items.Components.Wire>()?.ClearConnections(); }
            }
            box.RemoveWireInternal(box.Wires.Select(wire => wire.ID).ToArray());
            box.RemoveComponentInternal(box.Components.Select(component => component.ID).ToArray());
            box.RemoveLabelInternal(box.Labels.Select(label => label.ID).ToArray());
            EntityOps.Delete(box.Item.ContainedItems.Where(item => Array.BinarySearch(ids, item.ID) < 0).Select(item => item.ID).OrderBy(id => id).ToArray());
            if (contents != null)
            {
                XElement root = XElement.Parse(Encoding.UTF8.GetString(contents));
                foreach (XElement element in root.Elements().ToArray())
                {
                    if (EntityOps.Find((ushort)element.GetAttributeInt("ID", 0)) != null) { element.Remove(); }
                }
                if (root.HasElements && EntityOps.LoadExact(Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting)), owner) == null)
                { throw new InvalidOperationException("Circuit contents rollback failed"); }
            }
            box.LoadFromXML(state.FromPackage(box.Item.Prefab.ContentPackage));
        }
    }

    /// <summary>The steps of one circuit edit that surround the engine's event, and every entity id the frame touches.</summary>
    private sealed class CircuitWork
    {
        public readonly Item Box;
        public readonly List<BatchPayload.Sub> Before = new(), After = new();
        public readonly SortedSet<ushort> Touched = new();
        public ushort SubmarineId => SubRegistry.WireId(Box.Submarine);

        public CircuitWork(Item box) { Box = box; Touched.Add(box.ID); }

        /// <summary>A new backing item reaches every peer as a Spawn followed by its slot in the box.</summary>
        public void Created(Item item)
        {
            var spawn = new ByteWriter(256); var place = new ByteWriter(32);
            try
            {
                SpawnPayload.Write(ref spawn, SubmarineId, EntityOps.SaveXml(new MapEntity[] { item }));
                Before.Add(new BatchPayload.Sub(OpKind.Spawn, new[] { item.ID }, spawn.Written.ToArray()));
                if (EntityOps.CapturePlacement(item, out InventoryPlacement placement))
                {
                    InventoryPayload.Write(ref place, new[] { placement });
                    Before.Add(new BatchPayload.Sub(OpKind.Inventory, new[] { item.ID }, place.Written.ToArray()));
                }
            }
            finally { spawn.Dispose(); place.Dispose(); }
            Touched.Add(item.ID);
        }

        /// <summary>Items a removed component or wire leaves behind: deleted here and by the same frame everywhere else.</summary>
        public void Removed(IEnumerable<Item> items)
        {
            ushort[] doomed = items.Where(i => i != null && !i.Removed).Select(i => i.ID).Distinct().OrderBy(i => i).ToArray();
            if (doomed.Length == 0) { return; }
            After.Add(new BatchPayload.Sub(OpKind.Delete, doomed, Array.Empty<byte>()));
            foreach (ushort id in doomed) { Touched.Add(id); }
            EntityOps.Delete(doomed);
        }
    }

    /// <summary>Null when the request is invalid; the world is untouched in that case.</summary>
    private static byte[] Resolve(CircuitBox box, CircuitBoxOpcode opcode, IReadMessage message, CircuitWork work)
    {
        switch (opcode)
        {
            case CircuitBoxOpcode.AddComponent:
            {
                var request = INetSerializableStruct.Read<CircuitBoxAddComponentEvent>(message);
                ItemPrefab prefab = ItemPrefab.Prefabs.Find(p => p.UintIdentifier == request.PrefabIdentifier);
                ushort id = ICircuitBoxIdentifiable.FindFreeID(box.Components);
                if (prefab == null || id == ICircuitBoxIdentifiable.NullComponentID || !IsFinite(request.Position) || box.ComponentContainer == null) { return null; }
                if (box.ComponentContainer.Inventory.HowManyCanBePut(prefab) <= 0) { return null; }
                // The editor needs no resource in hand; the engine's own editor mode charges the generic one the same way.
                ItemPrefab resource = ItemPrefab.Prefabs[Tags.FPGACircuit];
                Item backing = CreateBackingItem(box.Item, prefab, box.ComponentContainer);
                if (backing == null) { return null; }
                box.AddComponentInternalUnsafe(id, backing, resource, request.Position);
                work.Created(backing);
                return Pack(opcode, new CircuitBoxServerCreateComponentEvent(backing.ID, resource.UintIdentifier, id, request.Position));
            }
            case CircuitBoxOpcode.DeleteComponent:
            {
                var request = INetSerializableStruct.Read<CircuitBoxRemoveComponentEvent>(message);
                List<CircuitBoxComponent> doomed = box.Components.Where(c => request.TargetIDs.Contains(c.ID)).ToList();
                if (doomed.Count == 0) { return null; }
                var items = new List<Item>(doomed.Select(c => c.Item));
                foreach (CircuitBoxWire wire in box.Wires)
                {
                    // Wires on a removed component go with it, backing item included.
                    if (doomed.Any(c => c.Connectors.Contains(wire.From) || c.Connectors.Contains(wire.To)) && wire.BackingWire.TryUnwrap(out Item wireItem)) { items.Add(wireItem); }
                }
                box.RemoveComponentInternal(request.TargetIDs);
                work.Removed(items);
                return Pack(opcode, request);
            }
            case CircuitBoxOpcode.MoveComponent:
            {
                var request = INetSerializableStruct.Read<CircuitBoxMoveComponentEvent>(message);
                if (!IsFinite(request.MoveAmount)) { return null; }
                box.MoveNodesInternal(request.TargetIDs, request.IOs, request.LabelIDs, request.MoveAmount);
                return Pack(opcode, request);
            }
            case CircuitBoxOpcode.AddWire:
                return ResolveWire(box, opcode, INetSerializableStruct.Read<CircuitBoxClientAddWireEvent>(message), work);
            case CircuitBoxOpcode.RemoveWire:
            {
                var request = INetSerializableStruct.Read<CircuitBoxRemoveWireEvent>(message);
                List<CircuitBoxWire> doomed = box.Wires.Where(w => request.TargetIDs.Contains(w.ID)).ToList();
                if (doomed.Count == 0) { return null; }
                var items = new List<Item>();
                foreach (CircuitBoxWire wire in doomed) { if (wire.BackingWire.TryUnwrap(out Item wireItem)) { items.Add(wireItem); } }
                box.RemoveWireInternal(request.TargetIDs);
                work.Removed(items);
                return Pack(opcode, request);
            }
            case CircuitBoxOpcode.AddLabel:
            {
                var request = INetSerializableStruct.Read<CircuitBoxAddLabelEvent>(message);
                ushort id = ICircuitBoxIdentifiable.FindFreeID(box.Labels);
                if (id == ICircuitBoxIdentifiable.NullComponentID || !IsFinite(request.Position)) { return null; }
                box.AddLabelInternal(id, request.Color, request.Position, request.Header, request.Body);
                return Pack(opcode, new CircuitBoxServerAddLabelEvent(id, request.Position, new Vector2(256), request.Color, request.Header, request.Body));
            }
            case CircuitBoxOpcode.RemoveLabel:
            {
                var request = INetSerializableStruct.Read<CircuitBoxRemoveLabelEvent>(message);
                box.RemoveLabelInternal(request.TargetIDs);
                return Pack(opcode, request);
            }
            case CircuitBoxOpcode.RenameLabel:
            {
                var request = INetSerializableStruct.Read<CircuitBoxRenameLabelEvent>(message);
                box.RenameLabelInternal(request.LabelId, request.Color, request.NewHeader, request.NewBody);
                return Pack(opcode, request);
            }
            case CircuitBoxOpcode.ResizeLabel:
            {
                var request = INetSerializableStruct.Read<CircuitBoxResizeLabelEvent>(message);
                if (!IsFinite(request.Position) || !IsFinite(request.Size)) { return null; }
                box.ResizeLabelInternal(request.ID, request.Position, request.Size);
                return Pack(opcode, request with { Size = Vector2.Max(request.Size, CircuitBoxLabelNode.MinSize) });
            }
            case CircuitBoxOpcode.RenameConnections:
            {
                var request = INetSerializableStruct.Read<CircuitBoxRenameConnectionLabelsEvent>(message);
                box.RenameConnectionLabelsInternal(request.Type, request.Override.ToDictionary());
                return Pack(opcode, request);
            }
            default:
                // Selections stay on the editor that made them (every editor's character has the same id), cursors are not edits.
                return null;
        }
    }

    /// <summary>
    /// The engine's CircuitBox.Connect, except that the wire's backing item is made here, under an id from the editor's window,
    /// instead of being queued on the entity spawner.
    /// </summary>
    private static byte[] ResolveWire(CircuitBox box, CircuitBoxOpcode opcode, CircuitBoxClientAddWireEvent request, CircuitWork work)
    {
        ItemPrefab prefab = ItemPrefab.Prefabs.Find(p => p.UintIdentifier == request.SelectedWirePrefabIdentifier);
        if (prefab == null || !request.Start.FindConnection(box).TryUnwrap(out CircuitBoxConnection one) || !request.End.FindConnection(box).TryUnwrap(out CircuitBoxConnection two)) { return null; }
        if (!CircuitBox.VerifyConnection(one, two)) { return null; }
        if (one.IsOutput == two.IsOutput) { return null; }
        CircuitBoxConnection start = one.IsOutput ? one : two, end = one.IsOutput ? two : one;
        ushort id = ICircuitBoxIdentifiable.FindFreeID(box.Wires);
        if (id == ICircuitBoxIdentifiable.NullComponentID) { return null; }

        Item backing = null;
        bool external = start is (CircuitBoxInputConnection or CircuitBoxOutputConnection) || end is (CircuitBoxInputConnection or CircuitBoxOutputConnection);
        if (external) { box.CreateWireWithoutItem(start, end, id, prefab); }
        else
        {
            if (box.WireContainer == null) { return null; }
            backing = CreateBackingItem(box.Item, prefab, box.WireContainer);
            if (backing == null) { return null; }
            box.CreateWireWithItem(start, end, id, backing);
            work.Created(backing);
        }
        Option<ushort> backingId = Option.None;
        if (backing != null) { backingId = Option.Some(backing.ID); }
        var resolved = request with { Start = CircuitBoxConnectorIdentifier.FromConnection(start), End = CircuitBoxConnectorIdentifier.FromConnection(end) };
        return Pack(opcode, new CircuitBoxServerCreateWireEvent(resolved, id, backingId));
    }

    /// <summary>A new item inside one of the box's containers, built through the editor's loader so its id comes from the editor's window.</summary>
    private static Item CreateBackingItem(Item box, ItemPrefab prefab, ItemContainer container)
    {
        if (!EntityOps.TryFindIdOffset(1, out int offset)) { return null; }
        var root = new XElement(EntityOps.SpawnRoot, new XElement("Item", new XAttribute("ID", 1), new XAttribute("name", prefab.Name.Value), new XAttribute("identifier", prefab.Identifier.Value), new XAttribute("rect", "0,0,1,1")));
        List<MapEntity> loaded = EntityOps.LoadAt(Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting)), box.Submarine ?? Submarine.MainSub, offset);
        if (loaded == null || loaded.Count != 1 || loaded[0] is not Item item) { return null; }
        // No inventory event: the frame carries the placement, and the engine's would name an item the clients do not have yet.
        if (container.Inventory.TryPutItem(item, null, null, createNetworkEvent: false)) { return item; }
        item.Remove();
        return null;
    }

    private static bool IsFinite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    private void AddCircuitChunks(Session s, ulong cut)
    {
        if (cut == 0) { return; }
        for (int i = 0; i < Item.ItemList.Count; i++)
        {
            Item item = Item.ItemList[i];
            if (item == null || item.Removed) { continue; }
            if (authority.Versions.Latest(item.ID) == 0) { continue; }
            for (int index = 0; index < item.Components.Count; index++)
            {
                if (item.Components[index] is not CircuitBox box) { continue; }
                byte[] captured;
                try { captured = CircuitOps.Capture(box); }
                catch (Exception e) { ModLog.Once($"subeditor: circuit box {item.ID} capture failed: {e.Message}"); continue; }
                if (captured == null) { continue; }
                var payload = new ByteWriter(512);
                var chunk = new ByteWriter(512);
                try
                {
                    CircuitPayload.Write(ref payload, (byte)index, captured);
                    byte[] frame = new byte[OpFrame.MaxEncodedSize(1, payload.Length)];
                    int length = OpFrame.Write(frame, OpKind.Circuit, ConsoleActor, cut, cut, new[] { item.ID }, payload.Written);
                    chunk.U8((byte)ChunkKind.Circuit); chunk.V(1); chunk.Bytes(frame.AsSpan(0, length));
                    if (chunk.Length > SubEditorProtocol.MaxChunkBytes)
                    {
                        ModLog.Error($"subeditor: circuit box {item.ID} is too large to snapshot");
                        s.Failed = true;
                        return;
                    }
                    AddChunk(s, chunk.Written.ToArray());
                }
                finally { payload.Dispose(); chunk.Dispose(); }
            }
        }
    }
}
