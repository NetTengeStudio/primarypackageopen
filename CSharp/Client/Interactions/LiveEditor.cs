using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    private readonly record struct TransformState(Guid Token, ulong Revision, string Position, string Unavailable, Submarine Frame);
    private readonly Dictionary<Item, TransformState> transformStates = new();
    private readonly List<MapEntity> editorCandidates = new(), editorSelection = new();
    private IDisposable editorOverlay;
    private bool editorActive, editorMove, editorMarquee, editorDrag, editorShowHidden, editorShowHulls, editorAwaitingTransform;
    private Vector2 editorAnchor, editorLastPointer = new(float.NaN);
    private readonly List<MovingItem> editorMoving = new(WorldEditJournal.MaxBatch);
    private Vector2 editorDelta;
    private double editorTransformDeadline;
    private readonly record struct MovingItem(Item Item, TransformState Before, Point Original);
    private Submarine editorFrame;
    private readonly List<MapEntity> editorHits = new();
    private int editorCycle;
    internal void SetEditorMove(bool move) => editorMove = move;
    internal bool ToggleEditorHidden() => editorShowHidden = !editorShowHidden;
    internal bool ToggleEditorHulls() => editorShowHulls = !editorShowHulls;
    private double nextEditorPick;
    private void InitializeLiveEditor() => editorOverlay = GUIRuntime.Instance.RegisterOverlay(() => editorActive && Core.PlayView.Running, DrawLiveEditor, StopLiveEditor, blocksWorld: true, cancelOnWindowOpen: false);
    internal void StopLiveEditor() { editorActive = editorDrag = editorMarquee = editorAwaitingTransform = false; editorMoving.Clear(); editorSelection.Clear(); editorHits.Clear(); }
    internal void ToggleLiveEditor() {
        if (editorActive) { StopLiveEditor(); views.CloseLiveEditor(); return; }
        if (!Allows(InteractionAction.ManageEntities)) { views.Error("Нет разрешения на редактирование мира"); return; }
        editorActive = true; editorLastPointer = new(float.NaN); views.OpenLiveEditorToolbar();
    }
    private void ReadWorldTransform(IReadMessage input) {
        Guid token = Guid.ParseExact(input.ReadString(), "N"); bool apply = input.ReadBoolean(); ulong revision = input.ReadUInt64();
        ushort nativeFrame = input.ReadUInt16(); string position = input.ReadString(), unavailable = input.ReadString();
        if (position.Length > 80 || unavailable.Length > 200) { throw new ArgumentException("Неверный снимок преобразования"); }
        var state = LiveItemTransform.Decode(position);
        if (!ResolveWorldItem(token, out Item item) || item.Removed || Entity.FindEntityByID(item.ID) != item) { return; }
        if (transformStates.TryGetValue(item, out var before) && before.Revision > revision) { return; }
        if ((item.Submarine?.ID ?? 0) != nativeFrame) { throw new InvalidOperationException("Система координат предмета ещё не согласована с сервером"); }
        if (before.Frame != null && LiveItemTransform.Decode(before.Position).Frame == state.Frame && before.Frame != item.Submarine) { throw new InvalidOperationException("Система координат предмета была заменена"); }
        if (apply) { string value = state.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + state.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture); LiveItemTransform.Validate(item, value, false); LiveItemTransform.Write(item, value); }
        transformStates[item] = new(token, revision, position, unavailable, item.Submarine);
    }
    private void PickEditor(Vector2 world) {
        WorldQueryService.Instance.QueryMap(world, world, editorCandidates); editorHits.Clear();
        foreach (MapEntity entity in editorCandidates) {
            if (entity.Removed || !editorShowHidden && entity.IsHidden || entity is Item { ParentInventory: not null }) { continue; }
            if (entity is Hull or Gap or WayPoint) { if (!editorShowHulls || !Submarine.RectContains(entity.WorldRect, world)) { continue; } }
            else if (!entity.IsMouseOn(world)) { continue; }
            editorHits.Add(entity);
        }
        editorHits.Sort(static (a, b) => { int depth = a.SpriteDepth.CompareTo(b.SpriteDepth); return depth != 0 ? depth : a.ID.CompareTo(b.ID); });
        if (editorCycle >= editorHits.Count) { editorCycle = 0; }
    }
    private void LiveWorldPressed(Vector2 pointer, bool primary, bool secondary) {
        if (editorAwaitingTransform) { return; }
        Vector2 world = GameMain.GameScreen.Cam.ScreenToWorld(pointer); PickEditor(world);
        if (secondary) { if (editorHits.Count > 0) { editorCycle = (editorCycle + 1) % editorHits.Count; } return; }
        if (!primary) { return; }
        bool shift = Keyboard.GetState().IsKeyDown(Keys.LeftShift) || Keyboard.GetState().IsKeyDown(Keys.RightShift);
        if (editorHits.Count == 0 || shift) {
            editorMarquee = true; editorAnchor = world; if (!shift) { editorSelection.Clear(); } return;
        }
        MapEntity selected = editorHits[editorCycle];
        if (!editorSelection.Contains(selected)) { editorSelection.Clear(); editorSelection.Add(selected); RequestSelectionTransforms(); }
        if (!editorMove) { views.InspectLiveEntity(selected); return; }
        editorMoving.Clear();
        if (selected is not Item selectedItem) { views.Error("Перемещение структурной геометрии ещё не включено"); return; }
        editorFrame = selectedItem.Submarine;
        bool missing = false;
        foreach (MapEntity entity in editorSelection) {
            if (entity is not Item item) { views.Error("Выделение содержит неподдерживаемую структурную геометрию"); editorMoving.Clear(); return; }
            string reason = LiveItemTransform.Unavailable(item); if (reason != null) { views.Error(item.Name + ": " + reason); editorMoving.Clear(); return; }
            if (item.Submarine != editorFrame) { views.Error("Выделите оборудование одной подлодки"); editorMoving.Clear(); return; }
            if (!transformStates.TryGetValue(item, out var before) || before.Frame != item.Submarine) {
                if (Multiplayer) {
                    missing = true;
                    editorMoving.Add(new(item, default, new Point(item.Rect.X, item.Rect.Y))); continue;
                }
                before = new(Guid.NewGuid(), 0, LiveItemTransform.Encode(Guid.NewGuid(), new Point(item.Rect.X, item.Rect.Y)), "", item.Submarine);
            }
            editorMoving.Add(new(item, before, LiveItemTransform.Decode(before.Position).Position));
        }
        if (missing) { RequestSelectionTransforms(); }
        editorAnchor = world - editorFrame.Position; editorDelta = Vector2.Zero; editorDrag = true;
    }
    private void UpdateLiveEditor() {
        if (!editorActive) { return; }
        if (!Allows(InteractionAction.ManageEntities) || !Core.PlayView.Running) { StopLiveEditor(); views.CloseLiveEditor(); return; }
        Vector2 pointer = GUIRuntime.Instance.Input.Pointer, world = GameMain.GameScreen.Cam.ScreenToWorld(pointer);
        for (int i = editorSelection.Count - 1; i >= 0; i--) { if (editorSelection[i].Removed) { editorSelection.RemoveAt(i); } }
        if (editorDrag || editorAwaitingTransform) {
            bool stale = editorFrame == null || editorFrame.Removed;
            foreach (var moving in editorMoving) { if (moving.Item.Removed || moving.Item.Submarine != editorFrame) { stale = true; break; } }
            if (stale) { editorDrag = editorAwaitingTransform = false; editorMoving.Clear(); return; }
            bool ready = RefreshMovingSnapshots();
            if (editorDrag) {
                Vector2 delta = world - editorFrame.Position - editorAnchor;
                Vector2 grid = Submarine.GridSize;
                editorDelta = new Vector2(MathF.Round(delta.X / grid.X) * grid.X, MathF.Round(delta.Y / grid.Y) * grid.Y);
                if (Mouse.GetState().LeftButton == ButtonState.Released) { editorDrag = false; editorAwaitingTransform = true; editorTransformDeadline = GUIRuntime.Now + 10; }
            }
            if (editorAwaitingTransform && (ready || editorDelta == Vector2.Zero || GUIRuntime.Now >= editorTransformDeadline)) {
                editorAwaitingTransform = false;
                try {
                    if (!ready && editorDelta != Vector2.Zero) { throw new InvalidOperationException("Серверные позиции выделения не получены за 10 секунд; перемещение не отправлено"); }
                    TranslateSelection();
                } catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { views.Error(error.Message); }
                editorMoving.Clear();
            }
        } else if (editorMarquee && Mouse.GetState().LeftButton == ButtonState.Released) {
            editorMarquee = false; WorldQueryService.Instance.QueryMap(editorAnchor, world, editorCandidates);
            foreach (MapEntity entity in editorCandidates) {
                if (editorSelection.Count >= 64) { break; }
                if (entity.Removed || entity is Item { ParentInventory: not null } || !editorShowHidden && entity.IsHidden || !editorShowHulls && entity is Hull or Gap or WayPoint) { continue; }
                if (!editorSelection.Contains(entity)) { editorSelection.Add(entity); }
            }
            RequestSelectionTransforms();
            if (editorSelection.Count == 1) { views.InspectLiveEntity(editorSelection[0]); }
        } else if (world != editorLastPointer || GUIRuntime.Now >= nextEditorPick) { nextEditorPick = GUIRuntime.Now + .1; editorLastPointer = world; editorCycle = 0; PickEditor(world); }
    }
    private bool RefreshMovingSnapshots() {
        bool ready = true;
        for (int i = 0; i < editorMoving.Count; i++) {
            var moving = editorMoving[i]; if (moving.Before.Token != Guid.Empty) { continue; }
            if (transformStates.TryGetValue(moving.Item, out var before) && before.Frame == editorFrame) {
                editorMoving[i] = new(moving.Item, before, LiveItemTransform.Decode(before.Position).Position);
            } else { ready = false; }
        }
        return ready;
    }
    private void DrawLiveEditor(SpriteBatch batch) {
        batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, Barotrauma.GUI.SamplerState, DepthStencilState.None, GameMain.ScissorTestEnable);
        try {
            var cam = GameMain.GameScreen.Cam;
            void Outline(Rectangle rect, Vector2 offset, Color color) {
                Vector2 a = cam.WorldToScreen(new Vector2(rect.X, rect.Y) + offset), b = cam.WorldToScreen(new Vector2(rect.Right, rect.Y - rect.Height) + offset);
                Barotrauma.GUI.DrawRectangle(batch, new Rectangle((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), Math.Max(1, (int)Math.Abs(b.X - a.X)), Math.Max(1, (int)Math.Abs(b.Y - a.Y))), color, false, thickness: 2);
            }
            foreach (MapEntity entity in editorSelection) { if (!entity.Removed) { Outline(entity.Rect, entity.Submarine?.Position ?? Vector2.Zero, GUITheme.Accent); } }
            if (!editorDrag && !editorAwaitingTransform && editorHits.Count > editorCycle) { MapEntity hover = editorHits[editorCycle]; if (!hover.Removed) { Outline(hover.Rect, hover.Submarine?.Position ?? Vector2.Zero, Color.White); } }
            if (editorDrag || editorAwaitingTransform) {
                foreach (var moving in editorMoving) { Outline(new Rectangle(moving.Original.X + (int)editorDelta.X, moving.Original.Y + (int)editorDelta.Y, moving.Item.Rect.Width, moving.Item.Rect.Height), editorFrame.Position, Color.LightGreen); }
            }
            if (editorMarquee) {
                Vector2 world = cam.ScreenToWorld(GUIRuntime.Instance.Input.Pointer);
                Outline(new Rectangle((int)Math.Min(editorAnchor.X, world.X), (int)Math.Max(editorAnchor.Y, world.Y), (int)Math.Abs(editorAnchor.X - world.X), (int)Math.Abs(editorAnchor.Y - world.Y)), Vector2.Zero, GUITheme.Accent);
            }
        } finally { batch.End(); }
    }
}

internal sealed partial class InteractionViews {
    private GUIWindow liveEditorToolbar;
    internal void OpenLiveEditorToolbar() {
        liveEditorToolbar?.Dispose();
        liveEditorToolbar = GUIRuntime.Instance.Windows.Open("Live Editor · F4", new Vector2(12, 12), 640, 94);
        var actions = liveEditorToolbar.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        actions.Add(new Button("Выбрать / свойства", _ => client.SetEditorMove(false)));
        actions.Add(new Button("Переместить", _ => client.SetEditorMove(true)));
        actions.Add(new Button("История", _ => OpenEditHistory()));
        actions.Add(new Button("Скрытые", button => { ((Button)button).Selected = client.ToggleEditorHidden(); button.Invalidate(GUIDirty.Style); }));
        actions.Add(new Button("Отсеки", button => { ((Button)button).Selected = client.ToggleEditorHulls(); button.Invalidate(GUIDirty.Style); }));
        liveEditorToolbar.Add(new Text("ЛКМ: объект · ПКМ: следующий под курсором · Shift: рамка · сетка подлодки") { Color = GUITheme.Muted, Wrap = true, Height = 32 });
        liveEditorToolbar.ClosingRequested += _ => client.StopLiveEditor();
    }
    internal void CloseLiveEditor() { liveEditorToolbar?.Dispose(); liveEditorToolbar = null; }
    internal void InspectLiveEntity(MapEntity entity) {
        if (entity is Item item) { OpenDevice(item); return; }
        var window = GUIRuntime.Instance.Windows.OpenContext(entity.GetType().Name, Pointer, 360);
        window.Add(new Text("Слой: " + entity.Layer));
        window.Add(new Text("Геометрия: " + entity.Rect));
        window.Add(new Text("Структурное изменение этого типа ещё не включено: требуется согласованная перестройка зависимостей и ранний барьер позднего входа.") { Wrap = true, Height = 100 });
    }
}
