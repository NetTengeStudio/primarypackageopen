using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Lights;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;
using PrimaryPackage.GUI;

namespace PrimaryPackage.SubEditor;

using BGUI = global::Barotrauma.GUI;

/// <summary>
/// Interaction fidelity fixes for the live SubEditor:
/// 1. Prevents camera zoom when scrolling the entity catalog / menu or any editor UI.
/// 2. Corrects item placement coordinates when the submarine is at live round coordinates (not Vector2.Zero).
/// 3. Corrects box multi-selection intersection math for submarine-relative entities.
/// 4. Enables Shift-click / Shift-drag to toggle or expand selection like standard editors.
/// 5. Corrects resize handles position and resizing coordinate math for submarine-relative entities.
/// </summary>
internal static class EditorInteractionPatches
{
    private static readonly FieldInfo PlacePositionField = AccessTools.Field(typeof(MapEntityPrefab), "placePosition");
    private static readonly FieldInfo SelectionPosField = AccessTools.Field(typeof(MapEntity), "selectionPos");
    private static readonly FieldInfo StartMovingPosField = AccessTools.Field(typeof(MapEntity), "startMovingPos");
    private static readonly PropertyInfo ResizingProp = typeof(MapEntity).GetProperty(nameof(MapEntity.Resizing), BindingFlags.Public | BindingFlags.Static);
    private static readonly FieldInfo ResizeDirXField = AccessTools.Field(typeof(MapEntity), "resizeDirX");
    private static readonly FieldInfo ResizeDirYField = AccessTools.Field(typeof(MapEntity), "resizeDirY");
    private static readonly FieldInfo PrevRectField = AccessTools.Field(typeof(MapEntity), "prevRect");
    private static readonly FieldInfo ResizedField = AccessTools.Field(typeof(MapEntity), "Resized");

    private static HashSet<MapEntity> preSelectingList;

    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("Camera.MoveCamera (SubEditor Zoom Guard)", () => harmony.Patch(
            AccessTools.Method(typeof(Camera), nameof(Camera.MoveCamera),
                new[] { typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) }),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(MoveCameraPrefix))));

        ModuleHost.Guard("ItemPrefab.UpdatePlacing (SubEditor Placing Coords)", () => harmony.Patch(
            AccessTools.Method(typeof(ItemPrefab), nameof(ItemPrefab.UpdatePlacing)),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(UpdatePlacingPrefix))));

        ModuleHost.Guard("MapEntity.FindSelectedEntities (SubEditor Multi-Select)", () => harmony.Patch(
            AccessTools.Method(typeof(MapEntity), nameof(MapEntity.FindSelectedEntities),
                new[] { typeof(Vector2), typeof(Vector2) }),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(FindSelectedEntitiesPrefix))));

        ModuleHost.Guard("MapEntity.UpdateSelecting (SubEditor Shift-Select)", () => harmony.Patch(
            AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateSelecting)),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(UpdateSelectingPrefix)),
            postfix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(UpdateSelectingPostfix))));

        ModuleHost.Guard("MapEntity.GetEditingHandlePos (SubEditor Handle Pos)", () => harmony.Patch(
            AccessTools.Method(typeof(MapEntity), "GetEditingHandlePos",
                new[] { typeof(int), typeof(int), typeof(Camera) }),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(GetEditingHandlePosPrefix))));

        ModuleHost.Guard("MapEntity.UpdateResizing (SubEditor Resizing Coords)", () => harmony.Patch(
            AccessTools.Method(typeof(MapEntity), "UpdateResizing",
                new[] { typeof(Camera) }),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(UpdateResizingPrefix))));

        ModuleHost.Guard("MapEntity.ClearHighlightedEntities (Keep Editor Hover)", () => harmony.Patch(
            AccessTools.Method(typeof(MapEntity), nameof(MapEntity.ClearHighlightedEntities)),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(ClearHighlightedEntitiesPrefix))));

        ModuleHost.Guard("Character.CanInteractWith (SubEditor Free Access)", () => harmony.Patch(
            AccessTools.Method(typeof(Character), nameof(Character.CanInteractWith),
                new[] { typeof(Item), typeof(float).MakeByRefType(), typeof(bool) }),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(CanInteractWithItemPrefix))));

        ModuleHost.Guard("Character.CanAccessInventory (SubEditor Free Access)", () => harmony.Patch(
            AccessTools.Method(typeof(Character), nameof(Character.CanAccessInventory),
                new[] { typeof(Inventory), typeof(CharacterInventory.AccessLevel) }),
            prefix: ModuleHost.Bind(typeof(EditorInteractionPatches), nameof(CanAccessInventoryPrefix))));
    }

    public static bool CanInteractWithItemPrefix(Character __instance, Item item, ref float distanceToItem, ref bool __result)
    {
        if (Screen.Selected is SubEditorScreen)
        {
            distanceToItem = 0.0f;
            __result = true;
            return false;
        }
        return true;
    }

    public static bool CanAccessInventoryPrefix(Character __instance, Inventory inventory, ref bool __result)
    {
        if (Screen.Selected is SubEditorScreen)
        {
            __result = true;
            return false;
        }
        return true;
    }

    public static bool ClearHighlightedEntitiesPrefix()
    {
        if (LiveEditorScreen.GameUpdating) { return false; }
        return true;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 1. Camera Zoom Guard
    // ----------------------------------------------------------------------------------------------------------------

    public static void MoveCameraPrefix(ref bool allowZoom)
    {
        if (!allowZoom) { return; }
        if (Screen.Selected is not SubEditorScreen editor) { return; }

        // Block zoom if mouse is over any active GUI element
        if (PrimaryPackage.GUI.GUIBridge.BlocksWorld || BGUI.MouseOn != null || GUIMessageBox.VisibleBox != null || Inventory.IsMouseOnInventory)
        {
            allowZoom = false;
            return;
        }

        Point mouse = PlayerInput.MousePosition.ToPoint();

        // Block zoom if mouse is over the EntityMenu (catalog list, category buttons, filter box)
        if (editor.EntityMenu != null && editor.EntityMenu.Visible && editor.EntityMenu.Rect.Contains(mouse))
        {
            allowZoom = false;
            return;
        }

        // Block zoom if mouse is over top panel or any editor panels
        if (editor.TopPanel != null && editor.TopPanel.Visible && editor.TopPanel.Rect.Contains(mouse))
        {
            allowZoom = false;
            return;
        }

        // Block zoom if mouse is over highlighted list box or property editing HUD
        if (MapEntity.EditingHUD != null && MapEntity.EditingHUD.Visible && MapEntity.EditingHUD.Rect.Contains(mouse))
        {
            allowZoom = false;
            return;
        }
        if (MapEntity.HighlightedListBox != null && MapEntity.HighlightedListBox.Visible && MapEntity.HighlightedListBox.Rect.Contains(mouse))
        {
            allowZoom = false;
            return;
        }

    }

    // ----------------------------------------------------------------------------------------------------------------
    // 2. Item Placing Coordinate Correction
    // ----------------------------------------------------------------------------------------------------------------

    public static bool UpdatePlacingPrefix(ItemPrefab __instance, Camera cam)
    {
        if (Screen.Selected is not LiveEditorScreen) { return true; }
        if (Submarine.MainSub == null) { return false; }

        if (PlayerInput.SecondaryMouseButtonClicked())
        {
            MapEntityPrefab.Selected = null;
            PlacePositionField.SetValue(null, Vector2.Zero);
            return false;
        }

        var potentialContainer = MapEntity.GetPotentialContainer(cam.ScreenToWorld(PlayerInput.MousePosition));
        Submarine owner = potentialContainer?.Submarine ?? Hull.FindHull(cam.ScreenToWorld(PlayerInput.MousePosition))?.Submarine ?? Submarine.MainSub;
        Vector2 position = Submarine.MouseToWorldGrid(cam, owner);
        Point subOffset = owner.Position.ToPoint();

        if (!__instance.ResizeHorizontal && !__instance.ResizeVertical)
        {
            if (PlayerInput.PrimaryMouseButtonClicked() && BGUI.MouseOn == null)
            {
                Rectangle itemRect = new Rectangle(
                    (int)position.X - subOffset.X,
                    (int)position.Y - subOffset.Y,
                    (int)(__instance.Sprite.size.X * __instance.Scale),
                    (int)(__instance.Sprite.size.Y * __instance.Scale));

                var item = new Item(itemRect, __instance, owner);
                item.SetTransform(ConvertUnits.ToSimUnits(item.Position), 0.0f);
                item.GetComponent<Barotrauma.Items.Components.Door>()?.RefreshLinkedGap();
                item.FindHull();
                item.Submarine = owner;
                if (item.body != null) { item.body.Submarine = owner; }

                if (PlayerInput.IsShiftDown())
                {
                    if (potentialContainer != null)
                    {
                        bool placed = false;
                        foreach (ItemInventory inventory in potentialContainer.OwnInventories)
                        {
                            int slot = inventory.FindAllowedSlot(item);
                            if (slot >= 0 && inventory.TryPutItem(item, slot, false, false, null, createNetworkEvent: false, triggerOnInsertedEffects: false)) { placed = true; break; }
                        }
                        SoundPlayer.PlayUISound(placed ? GUISoundType.PickItem : GUISoundType.PickItemFail);
                        if (!placed) { item.Remove(); PlacePositionField.SetValue(null, Vector2.Zero); return false; }
                    }
                }

                SubEditorScreen.StoreCommand(new AddOrDeleteCommand(new List<MapEntity> { item }, false));

                if (!PlayerInput.IsShiftDown())
                {
                    MapEntityPrefab.Selected = null;
                }

                PlacePositionField?.SetValue(null, Vector2.Zero);
                return false;
            }
        }
        else
        {
            Vector2 placeSize = __instance.Size * __instance.Scale;
            Vector2 currentPlacePos = PlacePositionField != null ? (Vector2)PlacePositionField.GetValue(null) : Vector2.Zero;

            if (currentPlacePos == Vector2.Zero)
            {
                if (PlayerInput.PrimaryMouseButtonHeld() && BGUI.MouseOn == null)
                {
                    PlacePositionField?.SetValue(null, position);
                }
            }
            else
            {
                if (__instance.ResizeHorizontal) { placeSize.X = Math.Max(position.X - currentPlacePos.X, __instance.Size.X); }
                if (__instance.ResizeVertical) { placeSize.Y = Math.Max(currentPlacePos.Y - position.Y, __instance.Size.Y); }

                if (PlayerInput.PrimaryMouseButtonReleased())
                {
                    Rectangle itemRect = new Rectangle(
                        (int)currentPlacePos.X - subOffset.X,
                        (int)currentPlacePos.Y - subOffset.Y,
                        (int)placeSize.X,
                        (int)placeSize.Y);

                    var item = new Item(itemRect, __instance, owner);
                    PlacePositionField?.SetValue(null, Vector2.Zero);

                    item.Submarine = owner;
                    item.SetTransform(ConvertUnits.ToSimUnits(item.Position), 0.0f);
                    item.FindHull();
                    item.Submarine = owner;
                    if (item.body != null) { item.body.Submarine = owner; }

                    SubEditorScreen.StoreCommand(new AddOrDeleteCommand(new List<MapEntity> { item }, false));

                    if (!PlayerInput.IsShiftDown())
                    {
                        MapEntityPrefab.Selected = null;
                    }

                    return false;
                }
            }
        }

        return false;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 3. Multi-Selection Box Selection (Coordinate Normalization)
    // ----------------------------------------------------------------------------------------------------------------

    public static bool FindSelectedEntitiesPrefix(Vector2 pos, Vector2 size, ref HashSet<MapEntity> __result)
    {
        if (Screen.Selected is not SubEditorScreen) { return true; }
        if (Submarine.MainSub == null || Submarine.MainSub.Position == Vector2.Zero) { return true; }

        HashSet<MapEntity> foundEntities = new HashSet<MapEntity>();
        Rectangle selectionRect = Submarine.AbsRect(pos, size);
        Quad2D selectionQuad = Quad2D.FromSubmarineRectangle(selectionRect);

        for (int _ei = 0; _ei < MapEntity.MapEntityList.Count; _ei++)
        {
            MapEntity entity = MapEntity.MapEntityList[_ei]; if (entity == null || !entity.SelectableInEditor || entity.Removed) { continue; }

            Quad2D entityQuad = entity.GetTransformedQuad();
            Quad2D testQuad;

            if (entity.Submarine != null && entity.Submarine.Position != Vector2.Zero)
            {
                Point subOffset = new Point((int)entity.Submarine.Position.X, (int)entity.Submarine.Position.Y);
                Rectangle localRect = new Rectangle(
                    selectionRect.X - subOffset.X,
                    selectionRect.Y - subOffset.Y,
                    selectionRect.Width,
                    selectionRect.Height);
                testQuad = Quad2D.FromSubmarineRectangle(localRect);
            }
            else
            {
                testQuad = selectionQuad;
            }

            if (testQuad.Intersects(entityQuad))
            {
                foundEntities.Add(entity);
                entity.IsIncludedInSelection = true;

                if (SubEditorScreen.IsLayerLinked(entity))
                {
                    ImmutableHashSet<MapEntity> entitiesInSameLayer = SubEditorScreen.GetEntitiesInSameLayer(entity);
                    foreach (MapEntity layerEntity in entitiesInSameLayer.Where(e => !foundEntities.Contains(e)))
                    {
                        foundEntities.Add(layerEntity);
                        layerEntity.IsIncludedInSelection = true;
                    }
                }
            }
        }

        __result = foundEntities;
        return false;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 4. Shift-Selection Support
    // ----------------------------------------------------------------------------------------------------------------

    public static bool UpdateSelectingPrefix()
    {
        preSelectingList = null;
        if (Screen.Selected is not SubEditorScreen) { return true; }
        if (Screen.Selected is LiveEditorScreen && (PrimaryPackage.GUI.GUIBridge.BlocksWorld ||
            (BGUI.MouseOn != null || Inventory.IsMouseOnInventory || GUIMessageBox.VisibleBox != null) &&
            MapEntity.StartMovingPos == Vector2.Zero && MapEntity.SelectionPos == Vector2.Zero && !MapEntity.Resizing)) { return false; }
        if (PlayerInput.IsShiftDown() && PlayerInput.PrimaryMouseButtonReleased()) { preSelectingList = new HashSet<MapEntity>(MapEntity.SelectedList); }
        return true;
    }

    public static void UpdateSelectingPostfix()
    {
        if (Screen.Selected is not SubEditorScreen || preSelectingList == null) { return; }

        if (PlayerInput.IsShiftDown() && PlayerInput.PrimaryMouseButtonReleased())
        {
            if (MapEntity.SelectedList.Count == 1 && preSelectingList.Count > 0)
            {
                MapEntity newlySelected = MapEntity.SelectedList.First();
                if (preSelectingList.Contains(newlySelected))
                {
                    preSelectingList.Remove(newlySelected);
                    MapEntity.DeselectAll();
                    foreach (MapEntity e in preSelectingList) { MapEntity.AddSelection(e); }
                }
                else
                {
                    foreach (MapEntity e in preSelectingList) { MapEntity.AddSelection(e); }
                }
            }
            else if (MapEntity.SelectedList.Count > 1 && preSelectingList.Count > 0)
            {
                foreach (MapEntity e in preSelectingList) { MapEntity.AddSelection(e); }
            }
        }

        preSelectingList = null;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // 5. Resize Handles Position & Resizing Coordinate Correction
    // ----------------------------------------------------------------------------------------------------------------

    public static bool GetEditingHandlePosPrefix(MapEntity __instance, int x, int y, Camera cam, ref Vector2 __result)
    {
        if (Screen.Selected is not SubEditorScreen) { return true; }
        if (__instance.Submarine == null || __instance.Submarine.Position == Vector2.Zero) { return true; }

        Vector2 handleDiff = new Vector2(x * (__instance.Rect.Width * 0.5f), y * (__instance.Rect.Height * 0.5f));
        var rotation = -__instance.RotationRad;
        handleDiff = MathUtils.RotatePoint(handleDiff, rotation);
        if (__instance.FlippedX) { handleDiff = handleDiff.FlipX(); }
        if (__instance.FlippedY) { handleDiff = handleDiff.FlipY(); }

        // Use WorldPosition instead of Position so the handle renders at the object's actual screen position
        __result = cam.WorldToScreen(__instance.WorldPosition + handleDiff);
        return false;
    }

    public static bool UpdateResizingPrefix(MapEntity __instance, Camera cam)
    {
        if (Screen.Selected is not SubEditorScreen) { return true; }
        if (__instance.Submarine == null || __instance.Submarine.Position == Vector2.Zero) { return true; }

        __instance.IsHighlighted = true;

        int startX = __instance.ResizeHorizontal ? -1 : 0;
        int startY = __instance.ResizeVertical ? -1 : 0;

        for (int x = startX; x < 2; x += 2)
        {
            for (int y = startY; y < 2; y += 2)
            {
                Vector2 handleDiff = new Vector2(x * (__instance.Rect.Width * 0.5f), y * (__instance.Rect.Height * 0.5f));
                var rotation = -__instance.RotationRad;
                handleDiff = MathUtils.RotatePoint(handleDiff, rotation);
                if (__instance.FlippedX) { handleDiff = handleDiff.FlipX(); }
                if (__instance.FlippedY) { handleDiff = handleDiff.FlipY(); }
                Vector2 handlePos = cam.WorldToScreen(__instance.WorldPosition + handleDiff);

                float highlightDist = 8 * BGUI.Scale;
                bool highlighted = Vector2.DistanceSquared(PlayerInput.MousePosition, handlePos) < highlightDist * highlightDist;

                if (highlighted && PlayerInput.PrimaryMouseButtonDown())
                {
                    SelectionPosField?.SetValue(null, Vector2.Zero);
                    ResizeDirXField?.SetValue(__instance, x);
                    ResizeDirYField?.SetValue(__instance, y);
                    ResizingProp?.SetValue(null, true);
                    StartMovingPosField?.SetValue(null, Vector2.Zero);
                    MapEntity.ClearHighlightedEntities();
                }
            }
        }

        if (MapEntity.Resizing)
        {
            Rectangle? prevRectVal = (Rectangle?)PrevRectField?.GetValue(__instance);
            if (prevRectVal == null)
            {
                prevRectVal = __instance.Rect;
                PrevRectField?.SetValue(__instance, prevRectVal);
            }

            Rectangle prevRect = prevRectVal.Value;
            Vector2 placePosition = prevRect.Location.ToVector2();
            Vector2 placeSize = prevRect.Size.ToVector2();

            // Crucial fix: translate world mouse coordinate into submarine-local space!
            Vector2 mousePos = cam.ScreenToWorld(PlayerInput.MousePosition) - __instance.Submarine.Position;
            Vector2 prevPos = placePosition;
            Vector2 prevOppositeCorner = prevPos + placeSize.FlipY();
            Vector2 prevCenter = placePosition + placeSize.FlipY() * 0.5f;
            mousePos = FlipThenRotate(mousePos, prevCenter, __instance.RotationRad, __instance.FlippedX, __instance.FlippedY);

            if (!PlayerInput.IsShiftDown())
            {
                mousePos = Submarine.VectorToWorldGrid(mousePos, Submarine.MainSub, round: true);
            }

            int dirX = ResizeDirXField != null ? (int)ResizeDirXField.GetValue(__instance) : 0;
            int dirY = ResizeDirYField != null ? (int)ResizeDirYField.GetValue(__instance) : 0;

            if (dirX > 0)
            {
                mousePos.X = Math.Max(mousePos.X, prevRect.X + Submarine.GridSize.X);
                placeSize.X = mousePos.X - placePosition.X;
            }
            else if (dirX < 0)
            {
                mousePos.X = Math.Min(mousePos.X, prevRect.Right - Submarine.GridSize.X);
                placeSize.X = MathF.Round((placePosition.X + placeSize.X) - mousePos.X);
                placePosition.X = MathF.Round(mousePos.X);
            }

            if (dirY < 0)
            {
                mousePos.Y = Math.Min(mousePos.Y, prevRect.Y - Submarine.GridSize.Y);
                placeSize.Y = placePosition.Y - mousePos.Y;
            }
            else if (dirY > 0)
            {
                mousePos.Y = Math.Max(mousePos.Y, prevRect.Y - prevRect.Height + Submarine.GridSize.Y);
                placeSize.Y = mousePos.Y - (prevRect.Y - prevRect.Height);
                placePosition.Y = mousePos.Y;
            }

            Vector2 newPos = placePosition;
            Vector2 newOppositeCorner = placePosition + placeSize.FlipY();

            Vector2 transformedCornerDiff = RotateThenFlip(newPos - prevPos, Vector2.Zero, -__instance.RotationRad, __instance.FlippedX, __instance.FlippedY);
            Vector2 transformedOppositeCornerDiff = RotateThenFlip(newOppositeCorner - prevOppositeCorner, Vector2.Zero, -__instance.RotationRad, __instance.FlippedX, __instance.FlippedY);

            Vector2 newPosTransformed = RotateThenFlip(prevPos, prevCenter, -__instance.RotationRad, __instance.FlippedX, __instance.FlippedY) + transformedCornerDiff;
            Vector2 newOppositeTransformed = RotateThenFlip(prevOppositeCorner, prevCenter, -__instance.RotationRad, __instance.FlippedX, __instance.FlippedY) + transformedOppositeCornerDiff;
            Vector2 newTransformedCenter = (newPosTransformed + newOppositeTransformed) * 0.5f;

            var newDiff = (newOppositeCorner - newPos) * 0.5f;
            placePosition = newTransformedCenter - newDiff;

            if ((int)placePosition.X != __instance.Rect.X || (int)placePosition.Y != __instance.Rect.Y || (int)placeSize.X != __instance.Rect.Width || (int)placeSize.Y != __instance.Rect.Height)
            {
                __instance.Rect = new Rectangle((int)placePosition.X, (int)placePosition.Y, (int)placeSize.X, (int)placeSize.Y);
            }

            if (!PlayerInput.PrimaryMouseButtonHeld())
            {
                ResizingProp?.SetValue(null, false);
                if (ResizedField?.GetValue(__instance) is Action<Rectangle> resizedAction)
                {
                    resizedAction(__instance.Rect);
                }

                var newData = new List<Rectangle> { __instance.Rect };
                var oldData = new List<Rectangle> { prevRect };
                SubEditorScreen.StoreCommand(new TransformCommand(new List<MapEntity> { __instance }, newData, oldData, true));

                if (__instance is Structure structure)
                {
                    foreach (var light in structure.Lights)
                    {
                        light.LightTextureTargetSize = __instance.Rect.Size.ToVector2();
                        light.Position = __instance.Rect.Location.ToVector2();
                    }
                }
                PrevRectField?.SetValue(__instance, null);
            }

            return false;
        }

        return false;
    }

    private static Vector2 FlipThenRotate(Vector2 point, Vector2 center, float angle, bool flipX, bool flipY)
    {
        if (flipX) { point = (point - center).FlipX() + center; }
        if (flipY) { point = (point - center).FlipY() + center; }
        return MathUtils.RotatePointAroundTarget(point, center, angle);
    }

    private static Vector2 RotateThenFlip(Vector2 point, Vector2 center, float angle, bool flipX, bool flipY)
    {
        point = MathUtils.RotatePointAroundTarget(point, center, angle);
        if (flipX) { point = (point - center).FlipX() + center; }
        if (flipY) { point = (point - center).FlipY() + center; }
        return point;
    }
}
