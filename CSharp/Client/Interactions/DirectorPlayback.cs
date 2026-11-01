using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

internal sealed class DirectorPlayback : IDisposable {
    private sealed class Playing {
        public DirectorEvent Event;
        public double Start, End, Next;
        public Sound Sound;
        public SoundChannel Channel;
        public Sprite Portrait;
        public void Dispose() { Channel?.Dispose(); Sound?.Dispose(); Portrait?.Remove(); Channel = null; Sound = null; Portrait = null; }
    }
    private readonly List<Playing> active = new(16);
    private readonly Queue<string> ids = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly IDisposable overlay;
    public int Count => active.Count;
    internal void Snapshot(List<InteractionClient.ActiveEffect> destination) {
        destination.Clear();
        foreach (var effect in active) { destination.Add(new InteractionClient.ActiveEffect { Id = effect.Event.Id, Actor = "local", Target = "", Kind = effect.Event.Kind, Name = effect.Event.Kind == DirectorKind.Caption ? effect.Event.Text : System.IO.Path.GetFileNameWithoutExtension(effect.Event.Resource), Remaining = (float)Math.Max(0, effect.End - GUIRuntime.Now) }); }
    }
    public DirectorPlayback() { overlay = GUIRuntime.Instance.RegisterOverlay(() => active.Count > 0, Draw); }
    public void Start(DirectorEvent value) {
        DirectorEvent.Validate(value);
        if (!seen.Add(value.Id)) { return; }
        ids.Enqueue(value.Id);
        if (ids.Count > 512) { seen.Remove(ids.Dequeue()); }
        if (value.Kind == DirectorKind.Stop) { Stop(); return; }
        if (value.Kind == DirectorKind.StopOne) {
            for (int i = active.Count - 1; i >= 0; i--) { if (active[i].Event.Id == value.Resource) { active[i].Dispose(); active.RemoveAt(i); } }
            return;
        }
        if (active.Count >= 16) { throw new InvalidOperationException("Уже выполняется 16 событий"); }
        double start = GUIRuntime.Now + value.Delay;
        active.Add(new Playing { Event = value, Start = start, End = start + value.Duration, Next = start });
    }
    public void Update() {
        double now = GUIRuntime.Now;
        for (int i = active.Count - 1; i >= 0; i--) {
            var effect = active[i];
            if (now >= effect.End) { effect.Dispose(); active.RemoveAt(i); continue; }
            if (now < effect.Start || effect.Event.Kind == DirectorKind.Caption || now < effect.Next) { continue; }
            if (effect.Event.Kind == DirectorKind.Scare && effect.Portrait == null) {
                try { effect.Portrait = CreaturePortrait.Load(CharacterPrefab.Prefabs[effect.Event.Visual.ToIdentifier()]); }
                catch (Exception error) { ModLog.Error("Director portrait: " + error.Message); effect.Dispose(); active.RemoveAt(i); continue; }
            }
            if (effect.Sound == null) {
                try { effect.Sound = GameMain.SoundManager?.LoadSound(DirectorCatalog.Sounds[effect.Event.Resource].Path, stream: false); }
                catch (Exception error) { ModLog.Error("Director sound: " + error.Message); effect.Dispose(); active.RemoveAt(i); continue; }
            }
            if (effect.Sound == null || effect.Sound.Loading) { continue; }
            if (effect.Channel?.IsPlaying == true) { continue; }
            effect.Channel?.Dispose();
            effect.Channel = effect.Sound.Play(effect.Event.Gain);
            effect.Next = now + effect.Event.Interval;
        }
    }
    private void Draw(SpriteBatch batch) {
        double now = GUIRuntime.Now;
        batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, Barotrauma.GUI.SamplerState, DepthStencilState.None, GameMain.ScissorTestEnable);
        try {
            var font = Fonts.FontRegistry.Get("departure_mono".ToIdentifier(), 18, true);
            int captionLine = 0;
            for (int i = 0; i < active.Count; i++) {
                var effect = active[i];
                if (now < effect.Start || effect.Event.Kind == DirectorKind.Sound) { continue; }
                float alpha = (float)Math.Clamp(Math.Min((now - effect.Start) / .2, (effect.End - now) / .35), 0, 1);
                if (effect.Event.Kind == DirectorKind.Scare) { Barotrauma.GUI.DrawRectangle(batch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Black * (alpha * effect.Event.Gain), true); }
                if (effect.Portrait is { } sprite) {
                    float fit = Math.Min(GameMain.GraphicsWidth * .6f / Math.Max(1, sprite.size.X), GameMain.GraphicsHeight * .65f / Math.Max(1, sprite.size.Y));
                    sprite.Draw(batch, new Vector2(GameMain.GraphicsWidth * .5f, GameMain.GraphicsHeight * .45f), Color.White * alpha, sprite.size * .5f, scale: fit);
                }
                string text = effect.Event.Text;
                if (text.Length == 0 || font == null) { continue; }
                float scale = Barotrauma.GUI.Scale;
                Vector2 size = font.MeasureString(text) * scale;
                if (size.X > GameMain.GraphicsWidth * .9f) { scale *= GameMain.GraphicsWidth * .9f / size.X; size = font.MeasureString(text) * scale; }
                Vector2 at = new((GameMain.GraphicsWidth - size.X) * .5f, effect.Event.Kind == DirectorKind.Scare ? GameMain.GraphicsHeight * .45f : GameMain.GraphicsHeight * .82f - captionLine++ * (size.Y + 8));
                Barotrauma.GUI.DrawRectangle(batch, new Rectangle((int)at.X - 8, (int)at.Y - 4, (int)size.X + 16, (int)size.Y + 8), Color.Black * (.8f * alpha), true);
                font.DrawString(batch, text, at, Color.White * alpha, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
            }
        } finally { batch.End(); }
    }
    public void Stop() { for (int i = 0; i < active.Count; i++) { active[i].Dispose(); } active.Clear(); }
    public void Dispose() { Stop(); overlay.Dispose(); seen.Clear(); ids.Clear(); }
}
