using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// The original Submarine Editor, hosted on top of a running round.
/// It derives from SubEditorScreen on purpose: all ~100 engine checks of the form "Screen.Selected is SubEditorScreen"
/// (wiring, inventories, relaxed item rules, layers, rendering flags) then behave exactly as in the standalone editor,
/// with no patches. GameMain.SubEditorScreen is pointed at this instance while it is open.
/// What differs from the standalone editor: no sub loading (the round's main sub is edited), the world keeps simulating,
/// and edits are sent to the server instead of staying local (see CommandBridge).
/// </summary>
internal sealed class LiveEditorScreen : SubEditorScreen
{
    private static readonly Action<Screen> ScreenSelect = NonVirtual(typeof(Screen).GetMethod(nameof(Select), Type.EmptyTypes));
    private static readonly Action<SubEditorScreen, Item> CallOpenItem =
        (Action<SubEditorScreen, Item>)Delegate.CreateDelegate(typeof(Action<SubEditorScreen, Item>),
            HarmonyLib.AccessTools.Method(typeof(SubEditorScreen), "OpenItem", new[] { typeof(Item) }));
    private static readonly Action<SubEditorScreen> CallCloseItem =
        (Action<SubEditorScreen>)Delegate.CreateDelegate(typeof(Action<SubEditorScreen>),
            HarmonyLib.AccessTools.Method(typeof(SubEditorScreen), "CloseItem", Type.EmptyTypes));

    private SubEditorScreen standalone;
    private Character previousControlled;
    private bool open;
    private LivePanel panel;
    private MetaPayload.Pair[] metadata;
    private bool metadataOpen;
    private long metadataCheck;

    public bool IsOpen => open;

    /// <summary>
    /// True while the round's own update runs inside this screen. The editor drives its dummy character itself (selection, opening containers,
    /// wiring); the round's update must not also treat that character as a player, or the same key press (E) selects and closes an item in one frame.
    /// </summary>
    internal static bool GameUpdating { get; private set; }
    internal static bool EditorUpdating { get; private set; }
    public static int CurrentFrame { get; private set; }

    /// <summary>
    /// The round can end while the editor is open (the lobby selects its screen, which deselects this one after the round already removed the dummy).
    /// The original then removes it a second time and logs an error; here it is forgotten first.
    /// </summary>
    public override void DeselectEditorSpecific()
    {
        if (dummyCharacter is { Removed: true }) { dummyCharacter = null; }
        CommitBulkItemBuffer();
        InventoryBridge.Flush();
        open = false;
        base.DeselectEditorSpecific();
    }

    /// <summary>The layer picked in the layer panel: new entities are placed into it.</summary>
    public string SelectedLayer => layerList?.SelectedData as string;

    /// <summary>Entity layers changed (here or by another editor): the layer list is rebuilt from the entities.</summary>
    public void RebuildLayers() => ReconstructLayers();

    public bool AddSubmarine(SubmarineInfo info)
    {
        if (info == null || info.IsFileCorrupted) { return false; }
        Vector2 at = Cam.Position;
        var request = new SubSpawn(info.Name, info.MD5Hash?.StringRepresentation ?? "", 1, 0, at.X, at.Y);
        var w = new ByteWriter(128);
        try
        {
            SubSpawnPayload.Write(ref w, request);
            return SubEditorClient.SendRequest(OpKind.SpawnSub, ReadOnlySpan<ushort>.Empty, w.Written, null);
        }
        finally { w.Dispose(); }
    }

    /// <summary>Calls Screen.Select without dispatching to SubEditorScreen.Select, which would load and own a submarine.</summary>
    private static Action<Screen> NonVirtual(MethodInfo method)
    {
        var dm = new DynamicMethod("pp_ScreenSelect", null, new[] { typeof(Screen) }, typeof(Screen), skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return (Action<Screen>)dm.CreateDelegate(typeof(Action<Screen>));
    }

    public override void Select() => Open();

    public bool Open()
    {
        if (open) { return true; }
        if (Submarine.MainSub == null || GameMain.GameSession == null) { return false; }
        try
        {
            standalone = GameMain.SubEditorScreen == this ? standalone : GameMain.SubEditorScreen;
            previousControlled = Character.Controlled;
            GameMain.SubEditorScreen = this;
            ScreenSelect(this);
            open = true;

            global::Barotrauma.GUI.PreventPauseMenuToggle = false;
            SetMode(Mode.Default);
            OpenEntityMenu(null);
            MainSub.UpdateTransform(interpolate: false);
            Cam.Position = MainSub.WorldPosition;
            Cam.UpdateTransform();
            CreateDummyCharacter();
            LiveHistory.Refresh();
            SetSubName();
            panel ??= new LivePanel(this);
            ReconstructLayers();
            RememberMetadata();
            return true;
        }
        catch (Exception e)
        {
            ModLog.Error($"subeditor: cannot open live editor: {e.Message}");
            Close();
            return false;
        }
    }

    /// <summary>Hands the screen back to the round. GameScreen.Select deselects this screen, which removes the dummy and clears editor state.</summary>
    public void Close()
    {
        if (standalone != null) { GameMain.SubEditorScreen = standalone; }
        if (open || Selected == this)
        {
            try { GameMain.GameScreen.Select(); }
            catch (Exception e) { ModLog.Error($"subeditor: round screen did not restore: {e.Message}"); }
        }
        if (previousControlled != null && !previousControlled.Removed) { Character.Controlled = previousControlled; }
        previousControlled = null;
    }

    /// <summary>The round keeps running: world first, then the editor's own input and GUI.</summary>
    public override void Update(double deltaTime)
    {
        CurrentFrame++;
        PrimaryPackage.GUI.GUIBridge.UpdateEditorInput();
        TrimInterface();
        Character controlled = Character.controlled == dummyCharacter ? previousControlled : Character.controlled;
        if (controlled != null && controlled != dummyCharacter) { previousControlled = controlled; }
        GameUpdating = true;
        Character.controlled = null;
        try { GameMain.GameScreen.Update(deltaTime); }
        finally
        {
            GameUpdating = false;
            if (open && Selected == this)
            {
                Character.controlled = dummyCharacter;
            }
            else if (controlled is { Removed: false })
            {
                Character.controlled = controlled;
            }
        }
        if (!open || Selected != this || SubEditorClient.State != LinkState.Live) { return; }
        EditorUpdating = true;
        try
        {
            HandleEditorInteractions();
            base.Update(deltaTime);
        }
        finally { EditorUpdating = false; }
        InventoryBridge.Flush();
        SyncMetadata();
    }

    private void HandleEditorInteractions()
    {
        if (global::Barotrauma.GUI.KeyboardDispatcher.Subscriber != null) { return; }

        // 1. 'E' Key to open/close containers & cabinets
        if (PlayerInput.KeyHit(InputType.Use) || PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.E))
        {
            if (dummyCharacter != null)
            {
                if (dummyCharacter.SelectedItem != null || OpenedItem != null)
                {
                    CallCloseItem(this);
                }
                else
                {
                    Vector2 mouseWorld = Cam.ScreenToWorld(PlayerInput.MousePosition);
                    Item target = MapEntity.HighlightedEntities.OfType<Item>().FirstOrDefault()
                        ?? MapEntity.MapEntityList.OfType<Item>().FirstOrDefault(it => it.SelectableInEditor && it.IsMouseOn(mouseWorld))
                        ?? MapEntity.SelectedList.OfType<Item>().FirstOrDefault();

                    if (target != null && !target.Removed)
                    {
                        var containers = target.GetComponents<Barotrauma.Items.Components.ItemContainer>();
                        if (containers.Any() || target.GetComponent<Barotrauma.Items.Components.CircuitBox>() != null)
                        {
                            CallOpenItem(this, target);
                        }
                    }
                }
            }
        }

        // 2. Quick Delete (Delete / Backspace)
        if (PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.Delete) || PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.Back))
        {
            if (Inventory.DraggingItems.Any())
            {
                var dragging = Inventory.DraggingItems.Where(it => it != null && !it.Removed).ToList();
                if (dragging.Any())
                {
                    InventoryBridge.Delete(dragging.Select(it => new InventorySlotItem(-1, it)).ToList());
                    for (int i = dragging.Count - 1; i >= 0; i--)
                    {
                        dragging[i].Drop(null);
                        dragging[i].Remove();
                    }
                    Inventory.DraggingItems.Clear();
                    SoundPlayer.PlayUISound(GUISoundType.DropItem);
                    return;
                }
            }

            if (MapEntity.SelectedAny)
            {
                var doomed = new List<MapEntity>(MapEntity.SelectedList);
                StoreCommand(new AddOrDeleteCommand(doomed, true));
                for (int i = doomed.Count - 1; i >= 0; i--) { if (!doomed[i].Removed) { doomed[i].Remove(); } }
                MapEntity.SelectedList.Clear();
                SoundPlayer.PlayUISound(GUISoundType.DropItem);
            }
            else
            {
                if (Inventory.selectedSlot is Inventory.SlotReference slotRef && slotRef.ParentInventory != null)
                {
                    Item slotItem = slotRef.ParentInventory.GetItemAt(slotRef.SlotIndex);
                    if (slotItem != null && !slotItem.Removed)
                    {
                        InventoryBridge.Delete(new[] { new InventorySlotItem(slotRef.SlotIndex, slotItem) });
                        slotItem.Drop(null);
                        slotItem.Remove();
                        SoundPlayer.PlayUISound(GUISoundType.DropItem);
                    }
                }
                else
                {
                    Vector2 mouseWorld = Cam.ScreenToWorld(PlayerInput.MousePosition);
                    MapEntity hover = MapEntity.HighlightedEntities.FirstOrDefault()
                        ?? MapEntity.MapEntityList.FirstOrDefault(e => e.SelectableInEditor && e.IsMouseOn(mouseWorld));
                    if (hover != null && !hover.Removed)
                    {
                        StoreCommand(new AddOrDeleteCommand(new List<MapEntity> { hover }, true));
                        hover.Remove();
                        SoundPlayer.PlayUISound(GUISoundType.DropItem);
                    }
                }
            }
        }
    }

    public void RememberMetadata()
    {
        if (Submarine.MainSub?.Info != null) { metadata = SubMeta.Capture(Submarine.MainSub.Info); }
    }

    private void SyncMetadata()
    {
        if (!SubEditorClient.Editing || Submarine.MainSub is not Submarine sub || metadata == null) { return; }
        bool visible = saveFrame != null;
        if (!visible && !metadataOpen) { return; }
        long now = Environment.TickCount64;
        if (visible && (PlayerInput.PrimaryMouseButtonHeld() || now - metadataCheck < 200)) { return; }
        metadataOpen = visible; metadataCheck = now;
        MetaPayload.Pair[] after = SubMeta.Capture(sub.Info), changes = SubMeta.Diff(metadata, after);
        if (changes.Length == 0) { return; }
        MetaPayload.Pair[] before = metadata;
        metadata = after;
        var writer = new ByteWriter(128);
        try
        {
            MetaPayload.Write(ref writer, changes);
            Action rollback = () => { SubMeta.Apply(sub.Info, SubMeta.CaptureKeysFrom(before, changes)); RememberMetadata(); };
            if (!SubEditorClient.SendRequest(OpKind.SubMeta, new[] { sub.ID }, writer.Written, rollback)) { rollback(); }
        }
        finally { writer.Dispose(); }
    }

    private GUIComponent trimmedFor;

    private void TrimInterface()
    {
        if (TopPanel == null || ReferenceEquals(trimmedFor, TopPanel)) { return; }
        trimmedFor = TopPanel;
        GUILayoutGroup row = TopPanel.Children.OfType<GUILayoutGroup>().FirstOrDefault();
        if (row == null) { return; }
        string back = TextManager.Get("back").Value, open = TextManager.Get("OpenSubButton").Value,
            test = TextManager.Get("TestSubButton").Value;
        foreach (GUIComponent child in row.Children.ToList())
        {
            if (child == linkedSubBox) { row.RemoveChild(child); continue; }
            if (child is not GUIButton button) { continue; }
            string tip = button.ToolTip?.SanitizedValue ?? "";
            if (tip == back)
            {
                button.ToolTip = RichString.Rich(new RawLString("Закрыть редактор (F4)"));
                button.OnClicked = (_, _) => { Close(); return true; };
            }
            else if (tip == open || tip == test) { row.RemoveChild(button); }
        }
        // What the removed buttons leave behind: separators at the start or two in a row.
        bool previousWasLine = true;
        foreach (GUIComponent child in row.Children.ToList())
        {
            bool line = child is GUIFrame { Style: { } style } && style.Name == "VerticalLine";
            if (line && previousWasLine) { row.RemoveChild(child); continue; }
            previousWasLine = line;
        }
        SetSubName();
    }

    private void SetSubName()
    {
        if (subNameLabel != null && MainSub?.Info != null) { subNameLabel.Text = ToolBox.LimitString(MainSub.Info.Name, subNameLabel.Font, subNameLabel.Rect.Width); }
    }

    public override void AddToGUIUpdateList()
    {
        if (open && dummyCharacter != null) { Character.controlled = dummyCharacter; }
        base.AddToGUIUpdateList();
        if (open) { panel?.AddToGUIUpdateList(); }
        PrimaryPackage.GUI.GUIBridge.UpdateListPostfix();
    }

    public override void Draw(double deltaTime, GraphicsDevice graphics, SpriteBatch spriteBatch) =>
        base.Draw(deltaTime, graphics, spriteBatch);
}
