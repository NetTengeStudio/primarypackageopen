using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    private sealed class ClientWorldProjection : IWorldEditTarget {
        private readonly System.Collections.Generic.Dictionary<WorldEditKey, (Item Item, Submarine Submarine, Guid Frame)> targets;
        private readonly bool simulation;
        internal ClientWorldProjection(Item[] items, WorldMutation[] mutations, bool simulation = false) {
            this.simulation = simulation;
            targets = new(mutations.Length);
            for (int i = 0; i < mutations.Length; i++) { targets.Add(mutations[i].Key, (items[i], items[i].Submarine, mutations[i].Key.Component == ushort.MaxValue ? LiveItemTransform.Decode(mutations[i].After).Frame : Guid.Empty)); }
        }
        private (Item Item, Submarine Submarine, Guid Frame) Resolve(in WorldEditKey key) {
            if (targets.TryGetValue(key, out var target) && !target.Item.Removed && Entity.FindEntityByID(target.Item.ID) == target.Item && target.Item.Submarine == target.Submarine) { return target; }
            throw new InvalidOperationException("Объект группового преобразования удалён, заменён или сменил подлодку");
        }
        public string Read(in WorldEditKey key) {
            var target = Resolve(key);
            if (key.Component == ushort.MaxValue) { return LiveItemTransform.Encode(target.Frame, new Point(target.Item.Rect.X, target.Item.Rect.Y)); }
            var field = Field(target.Item, key, out var component); return field.Read(component);
        }
        private static string Position(string state) { var p = LiveItemTransform.Decode(state).Position; return p.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        private static DeviceEditor.Field Field(Item item, in WorldEditKey key, out Barotrauma.Items.Components.ItemComponent component) {
            int split = key.Property.IndexOf('|'); if (split < 1) { throw new ArgumentException("Invalid component property address"); }
            component = DeviceEditor.Resolve(item, key.Component, key.Property[..split]); return DeviceEditor.Resolve(component, key.Property[(split + 1)..]);
        }
        public void Validate(in WorldEditKey key, string value) {
            var item = Resolve(key).Item;
            if (key.Component == ushort.MaxValue) { LiveItemTransform.Validate(item, Position(value), simulation); }
            else { Field(item, key, out _).Parse(value); }
        }
        public void Write(in WorldEditKey key, string value) {
            var item = Resolve(key).Item;
            if (key.Component == ushort.MaxValue) { LiveItemTransform.Write(item, Position(value)); }
            else { var field = Field(item, key, out var component); field.Write(component, value); }
        }
    }
    private void RequestSelectionTransforms() {
        if (!Multiplayer || editorSelection.Count == 0) { return; }
        int count = 0; foreach (var entity in editorSelection) { if (entity is Item) { count++; } }
        if (count == 0) { return; }
        Send(InteractionPacket.WorldTransformSelection, output => {
            output.WriteByte((byte)count); foreach (var entity in editorSelection) { if (entity is Item item) { output.WriteUInt16(item.ID); } }
        });
    }
    private void TranslateSelection() {
        if (editorDelta == Vector2.Zero) { return; }
        var rows = new TranslationWire.IntentRow[editorMoving.Count];
        var items = new Item[rows.Length]; var mutations = new WorldMutation[rows.Length];
        for (int i = 0; i < rows.Length; i++) {
            var moving = editorMoving[i]; var item = moving.Item; var before = moving.Before;
            if (item.Removed || item.Submarine != before.Frame || Multiplayer && (!deviceBindings.TryGetValue(before.Token, out var bound) || bound != item)) { throw new InvalidOperationException("Выделение удалено, заменено или сменило подлодку"); }
            var point = new Point(checked(moving.Original.X + (int)editorDelta.X), checked(moving.Original.Y + (int)editorDelta.Y));
            string after = LiveItemTransform.Encode(LiveItemTransform.Decode(before.Position).Frame, point);
            rows[i] = new(before.Token, before.Revision, before.Position, after);
            items[i] = item; mutations[i] = new(new(before.Token, ushort.MaxValue, LiveItemTransform.PositionKey), before.Revision, before.Position, after);
        }
        Guid request = Guid.NewGuid(); TranslationWire.Validate(request, rows);
        if (Multiplayer) {
            if (!Send(InteractionPacket.WorldTranslate, output => TranslationWire.WriteIntent(output, request, rows))) { throw new InvalidOperationException("Не удалось отправить перемещение"); }
            return;
        }
        var projection = new ClientWorldProjection(items, mutations, simulation: true);
        foreach (var mutation in mutations) {
            if (projection.Read(mutation.Key) != mutation.Before) { throw new InvalidOperationException("Положение выделения изменилось"); }
            projection.Validate(mutation.Key, mutation.After);
        }
        WorldEditJournal.Execute(mutations, true, projection);
    }
    private void ReadTranslationBatch(IReadMessage input) {
        var rows = TranslationWire.ReadState(input, out Guid request, out ulong revision);
        try {
            var items = new Item[rows.Length]; var mutations = new WorldMutation[rows.Length];
            int current = 0;
            for (int i = 0; i < rows.Length; i++) {
                var row = rows[i]; var position = LiveItemTransform.Decode(row.Position);
                if (!ResolveWorldItem(row.Token, out var item) || item.Removed || Entity.FindEntityByID(item.ID) != item || item.Submarine?.ID != row.NativeFrame) { throw new InvalidOperationException("Отсутствует привязка или система координат подтверждённого выделения"); }
                if (transformStates.TryGetValue(item, out var before)) {
                    if (before.Revision >= revision) { current++; }
                    if (before.Token != row.Token || before.Frame != item.Submarine) { throw new InvalidOperationException("Инкарнация выделения изменилась"); }
                }
                items[i] = item;
                string local = LiveItemTransform.Encode(position.Frame, new Point(item.Rect.X, item.Rect.Y));
                mutations[i] = new(new(row.Token, ushort.MaxValue, LiveItemTransform.PositionKey), 0, local, row.Position);
            }
            if (current == rows.Length) { return; }
            if (current != 0) { throw new InvalidOperationException("Часть группового подтверждения обогнана более новой ревизией"); }
            var projection = new ClientWorldProjection(items, mutations);
            foreach (var mutation in mutations) { projection.Validate(mutation.Key, mutation.After); }
            WorldEditJournal.Execute(mutations, true, projection);
            for (int i = 0; i < rows.Length; i++) { transformStates[items[i]] = new(rows[i].Token, revision, rows[i].Position, "", items[i].Submarine); }
        } catch (Exception error) {
            StopLiveEditor();
            string reason = error.Message.Length > 512 ? error.Message[..512] : error.Message;
            ModLog.Error("World projection " + request + ": " + error);
            Send(InteractionPacket.WorldEditFailure, output => { output.WriteString(request.ToString("N")); output.WriteString(reason); });
            views.Error("Не удалось согласовать групповое изменение мира: " + reason);
        }
    }
}
