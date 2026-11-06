using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Core;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    private Item lastLight;
    private int lastLightComponent = -1;
    private Vector2 lastLightPointer;
    private double lastLightClick;

    private void ResetLights() {
        LightEffectService.Instance.Clear();
        lastLight = null; lastLightComponent = -1; lastLightClick = 0;
    }
    private void FlickerPressed(Vector2 pointer) {
        if (!PlayView.Running || RPWheel.RPWheelMod.IsWheelOpen || !Allows(InteractionAction.FlickerLight)) { return; }
        var keyboard = Keyboard.GetState();
        bool alt = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
        if (!alt && Character.Controlled is { IsDead: false }) { return; }
        var camera = GameMain.GameScreen.Cam;
        float zoom = camera.Zoom;
        if (!float.IsFinite(zoom) || zoom <= 0) { return; }
        Vector2 world = camera.ScreenToWorld(pointer);
        float radius = 32 / zoom;
        float closest = radius * radius;
        if (!float.IsFinite(closest)) { return; }
        Item selected = null;
        int component = -1;
        // Selection runs only on an eligible click; no per-frame world scan or editor index.
        var items = Item.ItemList;
        for (int i = 0; i < items.Count; i++) {
            Item item = items[i];
            if (item.Removed) { continue; }
            var components = item.Components;
            for (int c = 0; c < components.Count && c <= byte.MaxValue; c++) {
                if (components[c] is not LightComponent { Light: { } light }) { continue; }
                float distance = Vector2.DistanceSquared(world, light.WorldPosition);
                if (!float.IsFinite(distance) || distance > closest) { continue; }
                closest = distance; selected = item; component = c;
            }
        }
        if (selected == null) { lastLight = null; return; }
        double now = GUIRuntime.Now;
        bool doubleClick = lastLight == selected && lastLightComponent == component &&
            now - lastLightClick <= Math.Clamp(GUIRuntime.Instance.Input.DoubleClickInterval, .1, .8) && Vector2.DistanceSquared(pointer, lastLightPointer) <= 36;
        if (alt || doubleClick) {
            lastLight = null;
            try { RequestFlicker(selected, (byte)component); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { views.Error(error.Message); }
        } else { lastLight = selected; lastLightComponent = component; lastLightClick = now; lastLightPointer = pointer; }
        GUIRuntime.Instance.Input.ConsumePointer();
    }
    private void RequestFlicker(Item item, byte component) {
        if (!Multiplayer) { LightEffectService.Instance.Start((LightComponent)item.Components[component], LightEffectService.FlickerDuration); return; }
        if (!Send(InteractionPacket.Flicker, output => { output.WriteUInt16(item.ID); output.WriteByte(component); })) {
            throw new InvalidOperationException("Не удалось отправить запрос мерцания серверу");
        }
    }
    private static void ReceiveFlicker(IReadMessage message) {
        ushort itemId = message.ReadUInt16();
        byte component = message.ReadByte();
        float duration = message.ReadSingle();
        Guid lease = Guid.ParseExact(message.ReadString(), "N");
        if (Entity.FindEntityByID(itemId) is Item { Removed: false } item && component < item.Components.Count && item.Components[component] is LightComponent light) {
            LightEffectService.Instance.Start(light, duration, lease);
        }
    }
}
