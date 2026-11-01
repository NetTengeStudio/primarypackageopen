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
        public PhantomRenderer.Instance Phantom;
        public bool Report;
        public void Dispose() { Phantom?.Dispose(); Phantom = null; Channel?.Dispose(); Sound?.Dispose(); Portrait?.Remove(); Channel = null; Sound = null; Portrait = null; }
    }
    private readonly List<Playing> active = new(16);
    private readonly Queue<string> ids = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly IDisposable overlay;
    private readonly Action<string, string> failed;
    private readonly List<(string Id, string Error)> failures = new(16);
    public int Count => active.Count;
    internal void Snapshot(List<InteractionClient.ActiveEffect> destination) {
        destination.Clear();
        foreach (var effect in active) { destination.Add(new InteractionClient.ActiveEffect { Id = effect.Event.Id, Actor = "local", Target = "", Kind = effect.Event.Kind, Name = DirectorCatalog.Describe(effect.Event), Remaining = (float)Math.Max(0, effect.End - GUIRuntime.Now) }); }
    }
    public DirectorPlayback(Action<string, string> failed) { this.failed = failed; overlay = GUIRuntime.Instance.RegisterOverlay(HasVisual, Draw); }
    private bool HasVisual() {
        double now = GUIRuntime.Now;
        for (int i = 0; i < active.Count; i++) {
            var value = active[i];
            if (now >= value.Start && (value.Event.Kind is DirectorKind.Scare or DirectorKind.Caption or DirectorKind.SignalCollapse || value.Event.Kind == DirectorKind.Hallucination && value.Event.Behavior == PhantomBehavior.Lagged || value.Event.Text.Length > 0)) { return true; }
        }
        return false;
    }
    public void Start(DirectorEvent value, bool report = true) {
        try { DirectorEvent.Validate(value); }
        catch (ArgumentException) { if (report && Guid.TryParseExact(value?.Id, "N", out _)) { failures.Add((value.Id, "Ресурсы или параметры эффекта недоступны на клиенте")); } throw; }
        if (seen.Contains(value.Id)) { return; }
        if (value.Kind == DirectorKind.Stop) { Remember(value.Id); Stop(); return; }
        if (value.Kind == DirectorKind.StopOne) {
            Remember(value.Id);
            for (int i = active.Count - 1; i >= 0; i--) { if (active[i].Event.Id == value.Resource) { active[i].Dispose(); active.RemoveAt(i); } }
            return;
        }
        if (active.Count >= 16) { throw new InvalidOperationException("Уже выполняется 16 событий"); }
        double start = GUIRuntime.Now + value.Delay;
        active.Add(new Playing { Event = value.Copy(), Start = start, End = start + value.Duration, Next = start, Report = report });
        Remember(value.Id);
    }
    private void Remember(string id) { seen.Add(id); ids.Enqueue(id); if (ids.Count > 512) { seen.Remove(ids.Dequeue()); } }
    public void Reset() { Stop(); seen.Clear(); ids.Clear(); }
    public void Update() {
        double now = GUIRuntime.Now;
        float requestedGain = 0;
        for (int i = 0; i < active.Count; i++) { var sound = active[i]; if (now >= sound.Start && now < sound.End && sound.Event.Resource.Length > 0) { requestedGain += sound.Event.Gain; } }
        float bus = .7f / Math.Max(.7f, requestedGain);
        for (int i = active.Count - 1; i >= 0; i--) {
            var effect = active[i];
            if (now >= effect.End) { effect.Dispose(); active.RemoveAt(i); continue; }
            if (now < effect.Start) { continue; }
            if (effect.Channel?.IsPlaying == true) { effect.Channel.Gain = effect.Event.Gain * bus * (float)Math.Clamp(Math.Min((now - effect.Start) / .1, (effect.End - now) / .2), 0, 1); }
            if (effect.Event.Kind == DirectorKind.Hallucination) {
                try {
                    effect.Phantom ??= new PhantomRenderer.Instance(effect.Event, effect.Start);
                    if (!effect.Phantom.Update(now)) { effect.Dispose(); active.RemoveAt(i); continue; }
                } catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException) {
                    ModLog.Error("Director hallucination: " + error.Message); Fail(effect, "Клиент не смог загрузить модель галлюцинации"); active.RemoveAt(i); continue;
                }
            }
            if (effect.Event.Kind == DirectorKind.Scare && effect.Portrait == null) {
                try { effect.Portrait = CreaturePortrait.Load(CharacterPrefab.Prefabs[effect.Event.Visual.ToIdentifier()]); }
                catch (Exception error) { ModLog.Error("Director portrait: " + error.Message); Fail(effect, "Клиент не смог загрузить изображение скримера"); active.RemoveAt(i); continue; }
            }
            if (effect.Event.Kind == DirectorKind.Caption || effect.Event.Resource.Length == 0 || now < effect.Next) { continue; }
            if (effect.Sound == null) {
                try { effect.Sound = GameMain.SoundManager?.LoadSound(DirectorCatalog.Sounds[effect.Event.Resource].Path, stream: false); }
                catch (Exception error) { ModLog.Error("Director sound: " + error.Message); Fail(effect, "Клиент не смог загрузить звук эффекта"); active.RemoveAt(i); continue; }
            }
            if (effect.Sound == null || effect.Sound.Loading) { continue; }
            if (effect.Channel?.IsPlaying == true) { continue; }
            effect.Channel?.Dispose();
            effect.Channel = effect.Sound.Play(effect.Event.Gain * bus);
            effect.Next = now + effect.Event.Interval;
        }
        for (int i = 0; i < failures.Count; i++) { var error = failures[i]; failed?.Invoke(error.Id, error.Error); }
        failures.Clear();
    }
    private void Fail(Playing effect, string error) { effect.Dispose(); if (effect.Report) { failures.Add((effect.Event.Id, error)); } }
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
                if (effect.Event.Kind == DirectorKind.SignalCollapse) {
                    HorrorSignalFrame signal = HorrorSignal.Sample(now - effect.Start, effect.Event.Duration, effect.Event.Intensity, (uint)effect.Event.Id[0] * 16777619u);
                    Barotrauma.GUI.DrawRectangle(batch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Black * signal.Darkness, true);
                    uint pattern = signal.Pattern;
                    for (int band = 0; band < 12; band++) {
                        pattern = pattern * 1664525u + 1013904223u;
                        int y = (int)(pattern % (uint)Math.Max(1, GameMain.GraphicsHeight));
                        int width = (int)(GameMain.GraphicsWidth * (.15f + (pattern & 255) / 512f));
                        int x = (int)((GameMain.GraphicsWidth - width) * ((pattern >> 8 & 255) / 255f));
                        Color interference = band % 3 == 0 ? new Color(80, 110, 100) : new Color(8, 8, 10);
                        Barotrauma.GUI.DrawRectangle(batch, new Rectangle(x + (int)(signal.Displacement * 18), y, width, 2 + band % 4), interference * (alpha * effect.Event.Intensity * .6f), true);
                    }
                }
                if (effect.Event.Kind == DirectorKind.Scare || effect.Event.Kind == DirectorKind.Hallucination && effect.Event.Behavior == PhantomBehavior.Lagged) { Barotrauma.GUI.DrawRectangle(batch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Black * (alpha * effect.Event.Intensity * .6f), true); }
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
    public void Stop() { failures.Clear(); for (int i = 0; i < active.Count; i++) { active[i].Dispose(); } active.Clear(); }
    public void Dispose() { Stop(); overlay.Dispose(); seen.Clear(); ids.Clear(); }
}
