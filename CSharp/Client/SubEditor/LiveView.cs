using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Particles;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// What the standalone editor has no reason to show but a live one must: the round's characters (the crew keeps moving while a submarine is
/// edited around them) and the other editors' cursors with their names. Both draw through the original editor's own draw calls, so it stays the original screen.
/// </summary>
internal static class LiveView
{
    private static readonly INet.NetChannel Channel = INet.Net.Channel(SubEditorProtocol.Channel);
    private readonly record struct CursorPacket(ulong Session, Vector2 Position, byte Flags, ushort Selected) : INet.INetPacket
    {
        public int SizeHintBytes => 20;
        public void Write(IWriteMessage output)
        {
            SubEditorProtocol.WriteClientHeader(output, ClientPacket.Cursor, Session);
            output.WriteSingle(Position.X); output.WriteSingle(Position.Y); output.WriteByte(Flags); output.WriteUInt16(Selected);
        }
    }
    private sealed class Peer { public Vector2 At; public long Seen; public ushort SelectedId; }

    private static readonly Dictionary<byte, Peer> Peers = new();
    private static readonly Dictionary<ushort, long> ConflictedEntities = new();

    public static void FlashConflict(ushort entityId)
    {
        if (entityId > 0) { ConflictedEntities[entityId] = Environment.TickCount64 + 2500; }
    }
    private static readonly Color[] Palette = { new(255, 170, 60), new(90, 200, 255), new(160, 255, 120), new(255, 110, 190), new(190, 150, 255), new(255, 235, 90) };
    private static long lastSent;
    private static Vector2 lastPos;
    private static bool announced;

    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("Submarine.DrawFront", () => harmony.Patch(
            AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawFront)), postfix: ModuleHost.Bind(typeof(LiveView), nameof(DrawCharacters))));
        ModuleHost.Guard("MapEntity.DrawEditor", () => harmony.Patch(
            AccessTools.Method(typeof(MapEntity), nameof(MapEntity.DrawEditor)), postfix: ModuleHost.Bind(typeof(LiveView), nameof(DrawLabels))));
    }

    public static void Reset() { Peers.Clear(); announced = false; }

    /// <summary>Other editors seen working in the last few seconds.</summary>
    public static int ActivePeers => Peers.Values.Count(p => Environment.TickCount64 - p.Seen <= PresenceFlags.ExpireMs);

    public static void OnPresence(IReadMessage m)
    {
        byte actor = m.ReadByte(), flags = m.ReadByte();
        float x = m.ReadSingle(), y = m.ReadSingle();
        ushort sel = m.BitPosition < m.LengthBits ? m.ReadUInt16() : (ushort)0;
        if ((flags & PresenceFlags.Closed) != 0) { Peers.Remove(actor); return; }
        if (!float.IsFinite(x) || !float.IsFinite(y)) { return; }
        if (!Peers.TryGetValue(actor, out Peer peer)) { Peers[actor] = peer = new Peer(); }
        peer.At = new Vector2(x, y); peer.Seen = Environment.TickCount64; peer.SelectedId = sel;
    }

    /// <summary>Reports this editor's cursor a few times a second while the editor is open, and once more when it closes.</summary>
    public static void Tick(bool editing, Camera cam, long now)
    {
        if (!editing)
        {
            if (announced)
            {
                announced = false;
                INet.Net.Send(Channel, new CursorPacket(SubEditorClient.SessionToken, Vector2.Zero, PresenceFlags.Closed, 0), DeliveryMethod.Unreliable);
            }
            return;
        }
        if (now - lastSent < PresenceFlags.SendIntervalMs) { return; }
        Vector2 world = cam.ScreenToWorld(PlayerInput.MousePosition);
        // Idle cursors still report once a second so the others do not time this one out.
        if (announced && Vector2.DistanceSquared(world, lastPos) < 4f && now - lastSent < 1000) { return; }
        lastSent = now; lastPos = world; announced = true;
        ushort selId = MapEntity.SelectedList.Count > 0 ? MapEntity.SelectedList.First().ID : (ushort)0;
        INet.Net.Send(Channel, new CursorPacket(SubEditorClient.SessionToken, world, 0, selId), DeliveryMethod.Unreliable);
    }

    private static bool Showing(out LiveEditorScreen screen)
    {
        screen = GameMain.SubEditorScreen as LiveEditorScreen;
        return screen != null && screen.IsOpen && Screen.Selected == screen;
    }

    /// <summary>
    /// Inside the editor's front batch (world space, back to front): the round's characters, without the editor's own invisible dummy,
    /// then the round's particles (muzzle flashes, sparks, smoke), which the original editor never draws.
    /// Deformable sprites and particles each need a batch of their own; the editor's batch is reopened as it was.
    /// </summary>
    private static void DrawCharacters(SpriteBatch spriteBatch, bool editing)
    {
        if (!editing || !Showing(out LiveEditorScreen screen)) { return; }
        bool anyDeformed = false;
        for (int i = Character.CharacterList.Count - 1; i >= 0; i--)
        {
            Character c = Character.CharacterList[i];
            if (!Drawable(c)) { continue; }
            if (Deformed(c)) { anyDeformed = true; continue; }
            Draw(spriteBatch, c, screen.Cam);
        }
        spriteBatch.End();
        Matrix view = screen.Cam.Transform;
        if (anyDeformed)
        {
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: view);
            for (int i = Character.CharacterList.Count - 1; i >= 0; i--)
            {
                Character c = Character.CharacterList[i];
                if (Drawable(c) && Deformed(c)) { Draw(spriteBatch, c, screen.Cam); }
            }
            spriteBatch.End();
        }
        DrawParticles(spriteBatch, view);
        spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, transformMatrix: view);
    }

    private static void DrawParticles(SpriteBatch spriteBatch, Matrix view)
    {
        var particles = GameMain.ParticleManager;
        if (particles == null) { return; }
        try
        {
            particles.UpdateTransforms();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: view);
            particles.Draw(spriteBatch, true, false, ParticleBlendState.AlphaBlend);
            particles.Draw(spriteBatch, true, true, ParticleBlendState.AlphaBlend);
            particles.Draw(spriteBatch, false, null, ParticleBlendState.AlphaBlend);
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, depthStencilState: DepthStencilState.None, transformMatrix: view);
            particles.Draw(spriteBatch, true, false, ParticleBlendState.Additive);
            particles.Draw(spriteBatch, true, true, ParticleBlendState.Additive);
            particles.Draw(spriteBatch, false, null, ParticleBlendState.Additive);
            spriteBatch.End();
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            ModLog.Once($"subeditor: particles could not be drawn: {e.Message}");
            try { spriteBatch.End(); } catch (InvalidOperationException) { }
        }
    }

    private static bool Drawable(Character c) => !c.Removed && c.Enabled && c.ID != Entity.DummyID && c.AnimController != null;

    private static bool Deformed(Character c) => c.AnimController.Limbs.Any(l => l.DeformSprite != null);

    private static void Draw(SpriteBatch spriteBatch, Character c, Camera cam)
    {
        try { c.Draw(spriteBatch, cam); }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException) { ModLog.Once($"subeditor: character {c.Name} could not be drawn: {e.Message}"); }
    }

    /// <summary>In screen space on top of the editor: names above the characters and the other editors' cursors.</summary>
    private static void DrawLabels(SpriteBatch spriteBatch, Camera cam)
    {
        if (!Showing(out _)) { return; }
        for (int i = 0; i < Character.CharacterList.Count; i++)
        {
            Character c = Character.CharacterList[i];
            if (c == null || c.Removed || !c.Enabled || c.ID == Entity.DummyID || string.IsNullOrEmpty(c.Name)) { continue; }
            Vector2 at = cam.WorldToScreen(c.DrawPosition + new Vector2(0f, 120f));
            if (at.X < -200 || at.Y < -50 || at.X > GameMain.GraphicsWidth + 200 || at.Y > GameMain.GraphicsHeight + 50) { continue; }
            global::Barotrauma.GUI.DrawString(spriteBatch, at - new Vector2(GUIStyle.SmallFont.MeasureString(c.Name).X / 2f, 0f), c.Name, c == Character.Controlled ? Color.White : Color.LightGray, Color.Black * 0.5f, 2, GUIStyle.SmallFont);
        }
        long now = Environment.TickCount64;
        if (ConflictedEntities.Count > 0)
        {
            List<ushort> expired = null;
            foreach (var (cId, expire) in ConflictedEntities)
            {
                if (now > expire) { (expired ??= new List<ushort>()).Add(cId); continue; }
                if (EntityOps.Find(cId) is MapEntity cEntity && !cEntity.Removed)
                {
                    Rectangle rect = cEntity.WorldRect;
                    Vector2 topLeft = cam.WorldToScreen(new Vector2(rect.X, rect.Y));
                    Vector2 bottomRight = cam.WorldToScreen(new Vector2(rect.Right, rect.Y - rect.Height));
                    Vector2 pos = new(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y));
                    Vector2 size = new(Math.Max(Math.Abs(bottomRight.X - topLeft.X), 8f), Math.Max(Math.Abs(bottomRight.Y - topLeft.Y), 8f));
                    float pulse = (float)Math.Sin((expire - now) / 80.0) * 0.4f + 0.6f;
                    global::Barotrauma.GUI.DrawRectangle(spriteBatch, pos, size, Color.Red * pulse, isFilled: false, depth: 0f, thickness: 3);
                }
            }
            if (expired != null) { foreach (ushort id in expired) { ConflictedEntities.Remove(id); } }
        }
        if (Peers.Count == 0) { return; }
        List<byte> stale = null;
        foreach (var pair in Peers)
        {
            if (now - pair.Value.Seen > PresenceFlags.ExpireMs) { (stale ??= new List<byte>()).Add(pair.Key); continue; }
            Color color = Palette[pair.Key % Palette.Length];
            if (pair.Value.SelectedId > 0 && EntityOps.Find(pair.Value.SelectedId) is MapEntity target && !target.Removed)
            {
                Rectangle rect = target.WorldRect;
                Vector2 topLeft = cam.WorldToScreen(new Vector2(rect.X, rect.Y));
                Vector2 bottomRight = cam.WorldToScreen(new Vector2(rect.Right, rect.Y - rect.Height));
                Vector2 pos = new(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y));
                Vector2 size = new(Math.Max(Math.Abs(bottomRight.X - topLeft.X), 6f), Math.Max(Math.Abs(bottomRight.Y - topLeft.Y), 6f));
                global::Barotrauma.GUI.DrawRectangle(spriteBatch, pos, size, color * 0.75f, isFilled: false, depth: 0f, thickness: 2);
            }
            Vector2 screen = cam.WorldToScreen(pair.Value.At);
            global::Barotrauma.GUI.DrawRectangle(spriteBatch, screen - new Vector2(5f), new Vector2(10f), color, isFilled: true);
            global::Barotrauma.GUI.DrawRectangle(spriteBatch, screen - new Vector2(7f), new Vector2(14f), Color.Black * 0.8f, isFilled: false);
            string name = GameMain.Client?.ConnectedClients.FirstOrDefault(client => client.SessionId == pair.Key)?.Name ?? $"Editor {pair.Key}";
            global::Barotrauma.GUI.DrawString(spriteBatch, screen + new Vector2(10f, -6f), name, color, Color.Black * 0.6f, 2, GUIStyle.SmallFont);
        }
        if (stale != null) { foreach (byte key in stale) { Peers.Remove(key); } }
    }
}
