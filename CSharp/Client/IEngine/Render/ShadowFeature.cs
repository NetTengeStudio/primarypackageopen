using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Lights;
using FarseerPhysics;
using HarmonyLib;
using IEngine.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace IEngine.Render;

sealed class ShadowFeature : Feature
{
    const float BlurUv = 0.005f, Extrude = 9000f, Pad = 2f;
    const int Ids = ushort.MaxValue + 1, Resample = 15;

    readonly record struct Box(float X0, float Y0, float X1, float Y1)
    {
        public bool Contains(Box b) => b.X0 >= X0 && b.Y0 >= Y0 && b.X1 <= X1 && b.Y1 <= Y1;
        public Box Grow(float d) => Grow(d, d);
        public Box Grow(float dx, float dy) => new(X0 - dx, Y0 - dy, X1 + dx, Y1 + dy);
        public bool Overlaps(Box b) => b.X0 <= X1 && b.X1 >= X0 && b.Y0 <= Y1 && b.Y1 >= Y0;
    }

    static ShadowFeature instance;
    static readonly ShadowField field = new();
    static readonly List<ConvexHull> casters = new(1024);
    static readonly int[] hiddenAt = new int[Ids], deepAt = new int[Ids], hullHiddenAt = new int[Ids], learnedAt = new int[Ids];
    static readonly List<Box> water = new(64);
    static readonly float[] spill = new float[Ids];
    static readonly Entity[] measured = new Entity[Ids];
    static readonly List<Structure> damageable = new(1024);
    static readonly StableKeyOrder damageOrder = new();
    static Vector2 view;
    static bool collecting, ready, verify;
    static int frame, drawBacks, cullOrdinal;
    static float mx, my, rx, ry, maxSpill;
    static Box screen;
    static bool wrapTop, wrapBottom, wrapLeft, wrapRight;
    static string maxSpillBy;
    static CostMeter cost;
    static long hiddenEntities, hiddenCharacters, hiddenLights, testedEntities, frames, checks, misses;
    static string missLog = "";

    public override string Name => "shadows";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        MethodInfo drawMap = AccessTools.Method(typeof(GameScreen), nameof(GameScreen.DrawMap));
        MethodInfo drawBack = AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawBack));
        int calls = 0;
        foreach (CodeInstruction ci in PatchProcessor.GetOriginalInstructions(drawMap)) { if (ci.opcode == OpCodes.Call && Equals(ci.operand, drawBack)) { calls++; } }
        cullOrdinal = calls == 3 ? 3 : 0;
        Patch(harmony, AccessTools.Method(typeof(ConvexHull), nameof(ConvexHull.CalculateLosVertices)), postfix: nameof(Caster));
        Patch(harmony, AccessTools.Method(typeof(LightManager), nameof(LightManager.UpdateObstructVision)), nameof(LosPre), nameof(LosPost));
        Patch(harmony, drawMap, finalizer: nameof(MapDone));
        Patch(harmony, drawBack, nameof(DrawBackPre));
        Patch(harmony, AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawFront)), nameof(DrawFrontPre));
        Patch(harmony, AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawDamageable)), nameof(DrawDamageablePre));
        Patch(harmony, AccessTools.Method(typeof(Character), nameof(Character.Draw)), nameof(CharacterPre), nameof(CharacterPost));
        Patch(harmony, AccessTools.Method(typeof(LightSource), nameof(LightSource.DrawSprite)), nameof(LightSpritePre));
        Patch(harmony, AccessTools.Method(typeof(LightSource), nameof(LightSource.DrawLightVolume)), nameof(LightVolumePre));
        return null;
    }

    static void Patch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null, string finalizer = null) =>
        harmony.Patch(target,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(ShadowFeature), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(ShadowFeature), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ShadowFeature), finalizer));

    public override void SetVerify(bool on) => verify = on;

    static void LosPre()
    {
        casters.Clear();
        collecting = instance.Active && Screen.Selected == GameMain.GameScreen;
    }

    static void Caster(ConvexHull __instance, Vector2 lightSourcePos)
    {
        if (!collecting || __instance.ShadowVertexCount == 0) { return; }
        view = lightSourcePos + (__instance.ParentEntity?.Submarine?.DrawPosition ?? Vector2.Zero);
        casters.Add(__instance);
    }

    static void LosPost(LightManager __instance, Camera cam)
    {
        if (!collecting) { return; }
        collecting = false;
        if (!__instance.LosEnabled || __instance.LosMode == LosMode.None || LightManager.ViewTarget == null || __instance.LosAlpha < 1f
            || casters.Count == 0 || Level.Loaded?.Renderer?.CollapseEffectStrength > 0f || Submarine.visibleEntities == null) { return; }
        long t0 = Stopwatch.GetTimestamp();
        Rectangle wv = cam.WorldView;
        RenderTarget2D los = __instance.LosTexture;
        mx = BlurUv * wv.Width + wv.Width / (float)los.Width + Pad;
        my = BlurUv * wv.Height + wv.Height / (float)los.Height + Pad;
        float fx = Math.Max(Math.Abs(wv.X - mx - view.X), Math.Abs(wv.Right + mx - view.X));
        float fy = Math.Max(Math.Abs(wv.Y + my - view.Y), Math.Abs(wv.Y - wv.Height - my - view.Y));
        field.Begin(view.X, view.Y, MathF.Sqrt(fx * fx + fy * fy));
        foreach (ConvexHull ch in casters) { AddCaster(ch); }
        field.End();
        frame++;
        frames++;
        drawBacks = 0;
        ready = true;
        Refraction(cam);
        foreach (Hull hull in Hull.HullList)
        {
            if (Bounds(hull, out Box box) && Covers(box)) { hullHiddenAt[hull.ID] = frame; }
        }
        foreach (MapEntity e in Submarine.visibleEntities)
        {
            testedEntities++;
            if (e is Hull || !Bounds(e, out Box box) || !Learned(e, ref box) || !(InHiddenHull(e, box) || Covers(box))) { continue; }
            hiddenAt[e.ID] = frame;
            hiddenEntities++;
            if (Unrefracted(box)) { deepAt[e.ID] = frame; }
        }
        foreach (Character c in Character.CharacterList)
        {
            if (c.IsVisible && CharacterBounds(c, out Box box) && Learned(c, ref box) && Covers(box) && Unrefracted(box)) { deepAt[c.ID] = frame; hiddenCharacters++; }
        }
        cost.Add(Stopwatch.GetTimestamp() - t0);
    }

    static void AddCaster(ConvexHull ch)
    {
        Vector2 offset = ch.ParentEntity?.Submarine?.DrawPosition ?? Vector2.Zero;
        Vector2 a = ch.losVertices[0].Pos + ch.losOffsets[0] + offset, b = ch.losVertices[1].Pos + ch.losOffsets[1] + offset;
        Vector2 da = Vector2.Normalize(a - view), db = Vector2.Normalize(b - view);
        float far = Extrude * MathF.Sqrt(Math.Max(0f, 1f + Vector2.Dot(da, db)) * 0.5f) - Pad;
        field.AddOccluder(a.X, a.Y, b.X, b.Y, far);
    }

    static void Refraction(Camera cam)
    {
        Rectangle wv = cam.WorldView;
        float blur = WaterRenderer.BlurAmount > 0f ? WaterRenderer.BlurAmount / 100f : 0f;
        rx = (0.5f * WaterRenderer.DistortionStrength.X * cam.Zoom + blur) * wv.Width + Pad;
        ry = (0.5f * WaterRenderer.DistortionStrength.Y * cam.Zoom + blur) * wv.Height + Pad;
        screen = new Box(wv.X, wv.Y - wv.Height, wv.Right, wv.Y);
        water.Clear();
        wrapTop = wrapBottom = wrapLeft = wrapRight = false;
        Box reach = screen.Grow(rx, ry);
        foreach (Hull hull in Hull.HullList)
        {
            if (hull.WaterVolume <= 0f && hull.drawSurface <= hull.Rect.Y - hull.Rect.Height || !Bounds(hull, out Box w) || !reach.Overlaps(w)) { continue; }
            water.Add(w);
            wrapTop |= w.Y0 <= screen.Y0 + ry;
            wrapBottom |= w.Y1 >= screen.Y1 - ry;
            wrapRight |= w.X0 <= screen.X0 + rx;
            wrapLeft |= w.X1 >= screen.X1 - rx;
        }
    }

    static bool Unrefracted(Box b)
    {
        if (!screen.Contains(b.Grow(mx, my))) { return false; }
        Box r = b.Grow(rx, ry);
        if (wrapTop && r.Y1 > screen.Y1 || wrapBottom && r.Y0 < screen.Y0 || wrapRight && r.X1 > screen.X1 || wrapLeft && r.X0 < screen.X0) { return false; }
        foreach (Box w in water) { if (w.Overlaps(r)) { return Covers(r); } }
        return true;
    }

    static bool Covers(Box b) => field.Covers(b.X0 - mx, b.Y0 - my, b.X1 + mx, b.Y1 + my);

    static bool IsHidden(Entity e) => ready && hiddenAt[e.ID] == frame;

    static bool IsDeep(Entity e) => ready && deepAt[e.ID] == frame;

    static bool Learned(Entity e, ref Box box)
    {
        if (measured[e.ID] != e) { return false; }
        box = box.Grow(spill[e.ID]);
        return true;
    }

    static bool InHiddenHull(MapEntity e, Box box) =>
        e is Item { CurrentHull: Hull h } && hullHiddenAt[h.ID] == frame && Bounds(h, out Box hb) && hb.Contains(box);

    static bool Bounds(MapEntity e, out Box b)
    {
        switch (e)
        {
            case Hull { Submarine: Submarine sub } hull:
                Rectangle r = hull.Rect;
                Vector2 s = sub.DrawPosition;
                b = new Box(r.X + s.X, r.Y - r.Height + s.Y, r.Right + s.X, r.Y + s.Y);
                return true;
            case Item item when !item.isWire && item.Prefab.DecorativeSprites.Length == 0 && item.cachedVisibleExtents is Rectangle x:
                Vector2 p = item.DrawPosition;
                b = new Box(p.X + x.X, p.Y + x.Y, p.X + x.Width, p.Y + x.Height);
                return true;
            case Structure st when st.Prefab.DecorativeSprites.Length == 0:
                RectangleF q = Quad2D.FromSubmarineRectangle(st.WorldRect).Rotated(st.FlippedX != st.FlippedY ? st.RotationRad : -st.RotationRad).BoundingAxisAlignedRectangle;
                Vector2 shift = st.DrawPosition - st.WorldPosition;
                b = new Box(q.X + shift.X, q.Y + shift.Y, q.Right + shift.X, q.Y + q.Height + shift.Y);
                return true;
            default:
                b = default;
                return false;
        }
    }

    static bool CharacterBounds(Character c, out Box b)
    {
        b = default;
        if (c.IsLocalPlayer) { return false; }
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (Limb limb in c.AnimController.Limbs)
        {
            if (limb.LightSource != null || limb.DeformSprite != null || limb.DecorativeSprites.Count > 0 || limb.ActiveSprite == null) { return false; }
            float r = Math.Max(ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent()), Reach(limb.ActiveSprite, limb.ActiveSprite.Origin, limb.Scale * limb.TextureScale));
            float wearScale = Math.Max(1f, limb.TextureScale) * Math.Max(1f, limb.Params.Scale) * Math.Max(1f, c.AnimController.RagdollParams.LimbScale);
            foreach (WearableSprite w in limb.WearingItems)
            {
                if (w.Sprite != null) { r = Math.Max(r, Reach(w.Sprite, w.InheritOrigin ? limb.ActiveSprite.Origin : w.Sprite.Origin, w.Scale * wearScale)); }
            }
            Vector2 p = limb.body.DrawPosition;
            (x0, y0, x1, y1) = (Math.Min(x0, p.X - r), Math.Min(y0, p.Y - r), Math.Max(x1, p.X + r), Math.Max(y1, p.Y + r));
        }
        b = new Box(x0, y0, x1, y1);
        return x0 <= x1;
    }

    static float Reach(Sprite s, Vector2 origin, float scale)
    {
        float ax = Math.Max(Math.Abs(origin.X), Math.Abs(s.size.X - origin.X)), ay = Math.Max(Math.Abs(origin.Y), Math.Abs(s.size.Y - origin.Y));
        return MathF.Sqrt(ax * ax + ay * ay) * scale;
    }

    static Exception MapDone(Exception __exception)
    {
        ready = false;
        return __exception;
    }

    static bool Sampled(Entity e) => verify || measured[e.ID] != e || learnedAt[e.ID] == frame || ((frame + e.ID) & Resample) == 0;

    static void Draw(SpriteBatch sb, MapEntity e, bool back)
    {
        bool hidden = back ? IsDeep(e) : IsHidden(e);
        if (hidden && !verify) { return; }
        if (e is Hull || !Sampled(e)) { e.Draw(sb, false, back); return; }
        int n0 = sb._batcher._batchItemCount;
        e.Draw(sb, false, back);
        if (Bounds(e, out Box box)) { Learn(sb, n0, e, box, hidden); }
    }

    static void Learn(SpriteBatch sb, int n0, Entity e, Box raw, bool hidden)
    {
        SpriteBatcher b = sb._batcher;
        if (b._batchItemCount < n0) { return; }
        float x0 = raw.X0, y0 = raw.Y0, x1 = raw.X1, y1 = raw.Y1;
        for (int i = n0; i < b._batchItemCount; i++)
        {
            SpriteBatchItem it = b._batchItemList[i];
            Grow(it.vertexTL.Position, ref x0, ref y0, ref x1, ref y1);
            Grow(it.vertexTR.Position, ref x0, ref y0, ref x1, ref y1);
            Grow(it.vertexBL.Position, ref x0, ref y0, ref x1, ref y1);
            Grow(it.vertexBR.Position, ref x0, ref y0, ref x1, ref y1);
        }
        float over = Math.Max(Math.Max(raw.X0 - x0, raw.Y0 - y0), Math.Max(x1 - raw.X1, y1 - raw.Y1));
        if (hidden)
        {
            checks++;
            if (over > spill[e.ID] + Pad && ++misses <= 8) { missLog += $"\n  {e.GetType().Name} '{e}' drew {over:F2} past its box, learned {spill[e.ID]:F2}"; }
        }
        if (measured[e.ID] != e) { measured[e.ID] = e; spill[e.ID] = 0; learnedAt[e.ID] = frame; }
        if (over <= spill[e.ID]) { return; }
        spill[e.ID] = over;
        if (over > maxSpill) { (maxSpill, maxSpillBy) = (over, $"{e.GetType().Name}:{(e as MapEntity)?.Prefab?.Identifier}"); }
    }

    static void Grow(Vector3 v, ref float x0, ref float y0, ref float x1, ref float y1)
    {
        x0 = Math.Min(x0, v.X);
        x1 = Math.Max(x1, v.X);
        y0 = Math.Min(y0, -v.Y);
        y1 = Math.Max(y1, -v.Y);
    }

    static bool DrawBackPre(SpriteBatch spriteBatch, bool editing, Predicate<MapEntity> predicate)
    {
        if (!ready || editing || ++drawBacks != cullOrdinal) { return true; }
        foreach (MapEntity e in Submarine.visibleEntities)
        {
            if (e.DrawBelowWater && (predicate == null || predicate(e))) { Draw(spriteBatch, e, true); }
        }
        return false;
    }

    static bool DrawFrontPre(SpriteBatch spriteBatch, bool editing, Predicate<MapEntity> predicate)
    {
        if (!ready || editing || GameMain.DebugDraw) { return true; }
        foreach (MapEntity e in Submarine.visibleEntities)
        {
            if (e.DrawOverWater && (predicate == null || predicate(e))) { Draw(spriteBatch, e, false); }
        }
        return false;
    }

    static bool DrawDamageablePre(SpriteBatch spriteBatch, Effect damageEffect, bool editing, Predicate<MapEntity> predicate)
    {
        if (!instance.Active) { return true; }
        damageable.Clear();
        foreach (MapEntity e in !editing && Submarine.visibleEntities != null ? Submarine.visibleEntities : MapEntity.MapEntityList)
        {
            if (e is Structure s && s.DrawDamageEffect && (predicate == null || predicate(e)) && (verify || !IsHidden(s))) { damageable.Add(s); }
        }
        damageOrder.Reset(damageable.Count);
        for (int i = 0; i < damageable.Count; i++)
        {
            int bits = BitConverter.SingleToInt32Bits(damageable[i].GetDrawDepth() + 0f);
            damageOrder.SetKey(i, ~(bits ^ ((bits >> 31) & int.MaxValue)));
        }
        damageOrder.Sort();
        for (int i = 0; i < damageable.Count; i++)
        {
            Structure s = damageable[damageOrder.IndexAt(i)];
            if (!ready || !Sampled(s)) { s.DrawDamage(spriteBatch, damageEffect, editing); continue; }
            int n0 = spriteBatch._batcher._batchItemCount;
            s.DrawDamage(spriteBatch, damageEffect, editing);
            if (Bounds(s, out Box box)) { Learn(spriteBatch, n0, s, box, IsHidden(s)); }
        }
        return false;
    }

    static bool CharacterPre(Character __instance, SpriteBatch spriteBatch, out int __state)
    {
        __state = -1;
        if (!ready) { return true; }
        bool hidden = IsDeep(__instance);
        if (hidden && !verify)
        {
            if (__instance.Enabled && !__instance.AnimController.SimplePhysicsEnabled) { __instance.AnimController.Collider?.UpdateDrawPosition(); }
            return false;
        }
        if (Sampled(__instance)) { __state = spriteBatch._batcher._batchItemCount; }
        return true;
    }

    static void CharacterPost(Character __instance, SpriteBatch spriteBatch, int __state)
    {
        if (__state >= 0 && CharacterBounds(__instance, out Box box)) { Learn(spriteBatch, __state, __instance, box, IsDeep(__instance)); }
    }

    static bool LightHidden(LightSource light)
    {
        if (!ready || verify) { return false; }
        float range = light.LightSourceParams.TextureRange;
        if (light.LightSprite != null)
        {
            Sprite s = light.LightSprite;
            float spriteRange = Math.Max(
                s.size.X * light.SpriteScale.X * (0.5f + Math.Abs(s.RelativeOrigin.X - 0.5f)),
                s.size.Y * light.SpriteScale.Y * (0.5f + Math.Abs(s.RelativeOrigin.Y - 0.5f)));
            range = Math.Max(Math.Max(spriteRange, Math.Max(light.LightTextureTargetSize.X, light.LightTextureTargetSize.Y)), range);
        }
        Vector2 p = light.Position + (light.ParentSub?.DrawPosition ?? Vector2.Zero);
        if (!Covers(new Box(p.X - range, p.Y - range, p.X + range, p.Y + range))) { return false; }
        hiddenLights++;
        return true;
    }

    static bool LightSpritePre(LightSource __instance) => !LightHidden(__instance);

    static bool LightVolumePre(LightSource __instance)
    {
        if (!LightHidden(__instance)) { return true; }
        if (!__instance.CastShadows) { return false; }
        __instance.CheckConvexHullsInRange();
        return __instance.NeedsRecalculation || __instance.state != LightSource.LightVertexState.UpToDate;
    }

    public override string Report() =>
        $"frames={frames} cull {cost} occluders={field.Occluders} drawBackOrdinal={cullOrdinal} hidden/tested entities={hiddenEntities}/{testedEntities} characters={hiddenCharacters} lights={hiddenLights} max spill {maxSpill:F2} ({maxSpillBy})" +
        (checks > 0 ? $" verify checks={checks} misses={misses}{missLog}" : "");
}
