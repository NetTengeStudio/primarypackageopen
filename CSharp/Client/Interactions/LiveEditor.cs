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
    private EditorAlignment? editorArrangement;
    private bool editorArrangementReady, editorSnap = true;
    private readonly List<EditorPoint> editorArranged = new(WorldEditJournal.MaxBatch);
    private double editorTransformDeadline;
    private readonly record struct MovingItem(Item Item, TransformState Before, Point Original);
    private Submarine editorFrame;
    private readonly List<MapEntity> editorHits = new();
    private int editorCycle;
    internal void SetEditorMove(bool move) { CancelEditorPreview(); editorMove = move; }
    internal bool ToggleEditorSnap() => editorSnap = !editorSnap;
    internal bool EditorSnap => editorSnap;
    internal bool EditorHidden => editorShowHidden;
    internal bool EditorHulls => editorShowHulls;
    internal void CancelEditorPreview() { editorArrangement = null; editorArrangementReady = editorDrag = editorMarquee = editorAwaitingTransform = false; editorMoving.Clear(); editorArranged.Clear(); }
    internal bool ToggleEditorHidden() => editorShowHidden = !editorShowHidden;
    internal bool ToggleEditorHulls() => editorShowHulls = !editorShowHulls;
    private double nextEditorPick;
    private void InitializeLiveEditor() => editorOverlay = GUIRuntime.Instance.RegisterOverlay(() => editorActive && Core.PlayView.Running, DrawLiveEditor, StopLiveEditor, blocksWorld: true, cancelOnWindowOpen: false, capturesMovement: true, moveCamera: MoveEditorCamera);
    internal void StopLiveEditor() { editorActive = false; EndEditorViewport(); CancelEditorPreview(); editorSelection.Clear(); editorHits.Clear(); }
    internal void ToggleLiveEditor() {
        if (editorActive) { StopLiveEditor(); views.CloseLiveEditor(); return; }
        if (!Allows(InteractionAction.ManageEntities)) { views.Error("Нет разрешения на редактирование мира"); return; }
        views.CloseLiveEditor();
        editorActive = true; BeginEditorViewport(); editorLastPointer = new(float.NaN); views.OpenLiveEditorToolbar();
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
        MapEntity previous = editorCycle < editorHits.Count ? editorHits[editorCycle] : null;
        WorldQueryService.Instance.QueryMap(world, world, editorCandidates); editorHits.Clear();
        foreach (MapEntity entity in editorCandidates) {
            if (entity.Removed || !editorShowHidden && entity.IsHidden || entity is Item { ParentInventory: not null }) { continue; }
            if (entity is Hull or Gap or WayPoint) { if (!editorShowHulls || !Submarine.RectContains(entity.WorldRect, world)) { continue; } }
            else if (!entity.IsMouseOn(world)) { continue; }
            editorHits.Add(entity);
        }
        editorHits.Sort(static (a, b) => { int depth = a.SpriteDepth.CompareTo(b.SpriteDepth); return depth != 0 ? depth : a.ID.CompareTo(b.ID); });
        int retained = previous == null ? -1 : editorHits.IndexOf(previous);
        editorCycle = retained >= 0 ? retained : 0;
    }
    private void LiveWorldPressed(Vector2 pointer, bool primary, bool secondary) {
        if (editorArrangement.HasValue) { if (secondary) { CancelEditorPreview(); } return; }
        if (editorAwaitingTransform) { return; }
        Vector2 world = GameMain.GameScreen.Cam.ScreenToWorld(pointer); PickEditor(world);
        if (secondary) { if (editorHits.Count > 0) { editorCycle = (editorCycle + 1) % editorHits.Count; } return; }
        if (!primary) { return; }
        bool shift = Keyboard.GetState().IsKeyDown(Keys.LeftShift) || Keyboard.GetState().IsKeyDown(Keys.RightShift);
        bool control = Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl);
        if (control && editorHits.Count > 0) {
            MapEntity target = editorHits[editorCycle];
            if (!editorSelection.Remove(target)) { if (editorSelection.Count == WorldEditJournal.MaxBatch) { views.Error("В выделении уже 64 объекта"); return; } editorSelection.Add(target); }
            RequestSelectionTransforms(); return;
        }
        if (editorHits.Count == 0 || shift) {
            editorMarquee = true; editorAnchor = world; if (!shift) { editorSelection.Clear(); } return;
        }
        MapEntity selected = editorHits[editorCycle];
        if (!editorSelection.Contains(selected)) { editorSelection.Clear(); editorSelection.Add(selected); RequestSelectionTransforms(); }
        if (!editorMove) { views.InspectLiveEntity(selected); return; }
        try { CaptureMovingSelection(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { views.Error(error.Message); return; }
        editorAnchor = world - editorFrame.Position; editorDelta = Vector2.Zero; editorDrag = true;
    }
    private void CaptureMovingSelection() {
        editorMoving.Clear(); editorArranged.Clear();
        if (editorSelection.Count == 0 || editorSelection[0] is not Item selectedItem) { throw new ArgumentException("Выделите оборудование одной подлодки"); }
        editorFrame = selectedItem.Submarine;
        bool missing = false;
        foreach (MapEntity entity in editorSelection) {
            if (entity is not Item item) { editorMoving.Clear(); throw new ArgumentException("Выделение содержит структурную геометрию, для которой преобразование ещё не реализовано"); }
            string reason = LiveItemTransform.Unavailable(item); if (reason != null) { editorMoving.Clear(); throw new InvalidOperationException(item.Name + ": " + reason); }
            if (item.Submarine != editorFrame) { editorMoving.Clear(); throw new ArgumentException("Выделите оборудование одной подлодки"); }
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
    }
    internal void PreviewEditorArrangement(EditorAlignment mode) {
        CancelEditorPreview();
        if (editorSelection.Count < (mode is EditorAlignment.DistributeX or EditorAlignment.DistributeY ? 3 : 2)) { throw new ArgumentException("Выделите несколько объектов рамкой"); }
        CaptureMovingSelection(); editorArrangement = mode; editorTransformDeadline = GUIRuntime.Now + 10;
        if (RefreshMovingSnapshots()) { BuildEditorArrangement(); }
    }
    private void BuildEditorArrangement() {
        Span<EditorRect> bounds = stackalloc EditorRect[editorMoving.Count]; Span<EditorPoint> positions = stackalloc EditorPoint[editorMoving.Count];
        for (int i = 0; i < bounds.Length; i++) { var moving = editorMoving[i]; bounds[i] = new(moving.Original.X, moving.Original.Y, moving.Item.Rect.Width, moving.Item.Rect.Height); }
        EditorGeometry.Arrange(bounds, positions, editorArrangement.Value);
        editorArranged.Clear(); foreach (var position in positions) { editorArranged.Add(position); }
        editorArrangementReady = true;
    }
    internal void CommitEditorArrangement() {
        if (!editorArrangement.HasValue || !editorArrangementReady) { throw new InvalidOperationException("Сначала подготовьте предпросмотр и дождитесь серверных позиций"); }
        TranslateSelection(); CancelEditorPreview();
    }
    private void UpdateLiveEditor() {
        if (!editorActive) { return; }
        if (!Allows(InteractionAction.ManageEntities) || !Core.PlayView.Running) { StopLiveEditor(); views.CloseLiveEditor(); return; }
        Vector2 pointer = GUIRuntime.Instance.Input.Pointer, world = GameMain.GameScreen.Cam.ScreenToWorld(pointer);
        for (int i = editorSelection.Count - 1; i >= 0; i--) { if (editorSelection[i].Removed) { editorSelection.RemoveAt(i); } }
        if (editorDrag || editorAwaitingTransform || editorArrangement.HasValue) {
            bool stale = editorFrame == null || editorFrame.Removed;
            foreach (var moving in editorMoving) { if (moving.Item.Removed || moving.Item.Submarine != editorFrame) { stale = true; break; } }
            if (stale) { CancelEditorPreview(); return; }
            bool ready = RefreshMovingSnapshots();
            if (editorArrangement.HasValue) {
                if (ready && !editorArrangementReady) {
                    try { BuildEditorArrangement(); }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException) { CancelEditorPreview(); views.Error(error.Message); }
                }
                else if (!ready && GUIRuntime.Now >= editorTransformDeadline) { CancelEditorPreview(); views.Error("Серверные позиции выделения не получены; выравнивание не отправлено"); }
                return;
            }
            if (editorDrag) {
                Vector2 delta = world - editorFrame.Position - editorAnchor;
                Vector2 grid = Submarine.GridSize;
                editorDelta = editorSnap ? new Vector2(EditorGeometry.Snap(delta.X, (int)grid.X), EditorGeometry.Snap(delta.Y, (int)grid.Y)) : new Vector2(MathF.Round(delta.X), MathF.Round(delta.Y));
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
        } else if (world != editorLastPointer || GUIRuntime.Now >= nextEditorPick) { nextEditorPick = GUIRuntime.Now + .1; editorLastPointer = world; PickEditor(world); }
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
            if (editorSnap) { DrawEditorGrid(batch, cam); }
            void Outline(Rectangle rect, Vector2 offset, Color color) {
                Vector2 a = cam.WorldToScreen(new Vector2(rect.X, rect.Y) + offset), b = cam.WorldToScreen(new Vector2(rect.Right, rect.Y - rect.Height) + offset);
                Barotrauma.GUI.DrawRectangle(batch, new Rectangle((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), Math.Max(1, (int)Math.Abs(b.X - a.X)), Math.Max(1, (int)Math.Abs(b.Y - a.Y))), color, false, thickness: 2);
            }
            foreach (MapEntity entity in editorSelection) { if (!entity.Removed) { Outline(entity.Rect, entity.Submarine?.Position ?? Vector2.Zero, GUITheme.Accent); } }
            if (!editorDrag && !editorAwaitingTransform && editorHits.Count > editorCycle) { MapEntity hover = editorHits[editorCycle]; if (!hover.Removed) { Outline(hover.Rect, hover.Submarine?.Position ?? Vector2.Zero, Color.White); } }
            if (editorDrag || editorAwaitingTransform || editorArrangementReady) {
                for (int i = 0; i < editorMoving.Count; i++) {
                    var moving = editorMoving[i]; int x = editorArrangementReady ? editorArranged[i].X : moving.Original.X + (int)editorDelta.X, y = editorArrangementReady ? editorArranged[i].Y : moving.Original.Y + (int)editorDelta.Y;
                    Outline(new Rectangle(x, y, moving.Item.Rect.Width, moving.Item.Rect.Height), editorFrame.Position, Color.LightGreen);
                }
            }
            if (editorMarquee) {
                Vector2 world = cam.ScreenToWorld(GUIRuntime.Instance.Input.Pointer);
                Outline(new Rectangle((int)Math.Min(editorAnchor.X, world.X), (int)Math.Max(editorAnchor.Y, world.Y), (int)Math.Abs(editorAnchor.X - world.X), (int)Math.Abs(editorAnchor.Y - world.Y)), Vector2.Zero, GUITheme.Accent);
            }
        } finally { batch.End(); }
    }
    private void DrawEditorGrid(SpriteBatch batch, Camera camera) {
        Vector2 a = camera.ScreenToWorld(Vector2.Zero), b = camera.ScreenToWorld(new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight));
        Vector2 origin = editorFrame is { Removed: false } ? editorFrame.Position : Submarine.MainSub?.Position ?? Vector2.Zero;
        float step = Math.Max(1, Submarine.GridSize.X);
        while (step * camera.Zoom < 24) { step *= 2; }
        float left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), bottom = Math.Min(a.Y, b.Y), top = Math.Max(a.Y, b.Y);
        // At least 24 screen pixels per line; the cap also bounds pathological resolutions/zoom.
        float firstX = MathF.Ceiling((left - origin.X) / step) * step + origin.X, firstY = MathF.Ceiling((bottom - origin.Y) / step) * step + origin.Y;
        for (int i = 0; i < 256; i++) {
            float x = firstX + i * step; if (x > right) { break; }
            Barotrauma.GUI.DrawLine(batch, camera.WorldToScreen(new Vector2(x, bottom)), camera.WorldToScreen(new Vector2(x, top)), GUITheme.Border * .4f);
        }
        for (int i = 0; i < 256; i++) {
            float y = firstY + i * step; if (y > top) { break; }
            Barotrauma.GUI.DrawLine(batch, camera.WorldToScreen(new Vector2(left, y)), camera.WorldToScreen(new Vector2(right, y)), GUITheme.Border * .4f);
        }
    }
}

internal sealed partial class InteractionViews {
    private GUIWindow liveEditorToolbar, liveEditorAlignment;
    internal void OpenLiveEditorToolbar() {
        liveEditorToolbar?.Dispose();
        liveEditorToolbar = GUIRuntime.Instance.Windows.Open("Редактор подлодки · F4", new Vector2(12, 12), 740, 162);
        liveEditorToolbar.AllowsViewportInputOutside = true;
        var actions = liveEditorToolbar.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        actions.Add(new Button("Выбрать / свойства", _ => client.SetEditorMove(false)));
        actions.Add(new Button("Переместить", _ => client.SetEditorMove(true)));
        actions.Add(new Button("Выровнять", _ => OpenEditorAlignment()));
        actions.Add(new Button("История", _ => OpenEditHistory()));
        var options = liveEditorToolbar.Add(new Panel { Flow = GUIFlow.Horizontal, Height = GUITheme.ButtonHeight });
        options.Add(new Button("Сетка / привязка", button => { ((Button)button).Selected = client.ToggleEditorSnap(); button.Invalidate(GUIDirty.Style); }) { Selected = client.EditorSnap });
        options.Add(new Button("Скрытые", button => { ((Button)button).Selected = client.ToggleEditorHidden(); button.Invalidate(GUIDirty.Style); }) { Selected = client.EditorHidden });
        options.Add(new Button("Отсеки", button => { ((Button)button).Selected = client.ToggleEditorHulls(); button.Invalidate(GUIDirty.Style); }) { Selected = client.EditorHulls });
        liveEditorToolbar.Add(new Text("ЛКМ: объект · ПКМ: перебор · Ctrl: добавить / убрать · Shift: рамка · Escape: отмена") { Color = GUITheme.Muted, Wrap = true, Height = 32 });
        liveEditorToolbar.Add(new Text("Камера: WASD / стрелки · средняя кнопка: панорама · колесо: масштаб") { Color = GUITheme.Muted, Wrap = true, Height = 32 });
        liveEditorToolbar.ClosingRequested += _ => client.StopLiveEditor();
    }
    internal void CloseLiveEditor() { liveEditorAlignment?.Dispose(); liveEditorAlignment = null; liveEditorToolbar?.Dispose(); liveEditorToolbar = null; }
    private void OpenEditorAlignment() {
        liveEditorAlignment?.Dispose();
        liveEditorAlignment = GUIRuntime.Instance.Windows.Open("Выравнивание выделения", new Vector2(12, 180), 330, 245);
        liveEditorAlignment.AllowsViewportInputOutside = true;
        var mode = liveEditorAlignment.Add(new Dropdown(new[] { "По левому краю", "По центру X", "По правому краю", "По верхнему краю", "По центру Y", "По нижнему краю", "Распределить центры по X", "Распределить центры по Y" }));
        liveEditorAlignment.Add(new Text("Выберите объекты рамкой. Зелёный контур показывает результат; подлодка изменится после применения.") { Wrap = true, Height = 72 });
        liveEditorAlignment.Add(new Button("Предпросмотр", _ => SceneTry(() => client.PreviewEditorArrangement((EditorAlignment)mode.SelectedIndex))));
        liveEditorAlignment.Add(new Button("Применить", _ => SceneTry(client.CommitEditorArrangement)));
        liveEditorAlignment.Add(new Button("Отменить предпросмотр", _ => client.CancelEditorPreview()));
        liveEditorAlignment.ClosingRequested += _ => client.CancelEditorPreview();
    }
    internal void InspectLiveEntity(MapEntity entity) {
        if (entity is Item item) { OpenDevice(item); return; }
        var window = GUIRuntime.Instance.Windows.OpenContext(entity.GetType().Name, Pointer, 360);
        window.Add(new Text("Слой: " + entity.Layer));
        window.Add(new Text("Геометрия: " + entity.Rect));
        window.Add(new Text("Структурное изменение этого типа ещё не включено: требуется согласованная перестройка зависимостей и ранний барьер позднего входа.") { Wrap = true, Height = 100 });
    }
}
