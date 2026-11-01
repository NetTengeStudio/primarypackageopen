using System;
using System.Runtime.InteropServices;
using Barotrauma;
using FarseerPhysics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.MotionBlur;

public sealed class MotionScan {
    private const float MaxCharacterShare = 0.2f;
    private const float VelocityRate = 25.0f;

    private readonly MotionField field = new MotionField();

    private Vector2 previousCenter;
    private Vector2 cameraVelocity;
    private bool primed;

    public int ScreenHeight { get; private set; }

    public float FrameShift { get; private set; }

    public void Reset() {
        primed = false;
        cameraVelocity = Vector2.Zero;
    }

    public bool Sample(Camera camera, float seconds, out MotionSample sample) {
        sample = default;
        if (camera == null || seconds <= 0.0f) { return false; }
        int screenWidth = camera.Resolution.X;
        int screenHeight = camera.Resolution.Y;
        int viewWidth = camera.WorldView.Width;
        if (screenWidth <= 0 || screenHeight <= 0 || viewWidth <= 0) { return false; }
        ScreenHeight = screenHeight;
        float zoom = screenWidth / (float)viewWidth;
        Vector2 center = camera.WorldViewCenter;
        if (!primed) {
            previousCenter = center;
            primed = true;
            return false;
        }
        Vector2 delta = center - previousCenter;
        previousCenter = center;
        FrameShift = delta.Length() * zoom;
        float blend = PrimaryPackage.Core.Scalar.Decay(VelocityRate, seconds);
        cameraVelocity += (delta / seconds - cameraVelocity) * blend;
        field.Begin(cameraVelocity.X, cameraVelocity.Y, zoom);
        float screenArea = screenWidth * (float)screenHeight;
        float covered = CollectSubmarines(center, zoom, screenWidth, screenHeight, screenArea);
        field.Add(0.0f, 0.0f, covered >= 1.0f ? 0.0f : 1.0f - covered);
        CollectCharacters(center, zoom, screenWidth, screenHeight, screenArea);
        sample = field.Resolve();
        return true;
    }

    private float CollectSubmarines(Vector2 center, float zoom, int screenWidth, int screenHeight, float screenArea) {
        Span<Submarine> subs = CollectionsMarshal.AsSpan(Submarine.Loaded);
        float covered = 0.0f;
        for (int i = 0; i < subs.Length; i++) {
            Submarine sub = subs[i];
            if (sub?.SubBody == null) { continue; }
            Rectangle borders = sub.VisibleBorders;
            if (borders.Width <= 0 || borders.Height <= 0) { continue; }
            Vector2 origin = sub.WorldPosition;
            float left = (borders.X + origin.X - center.X) * zoom + screenWidth * 0.5f;
            float top = -(borders.Y + origin.Y - center.Y) * zoom + screenHeight * 0.5f;
            float share = Overlap(left, top, borders.Width * zoom, borders.Height * zoom,
                screenWidth, screenHeight, screenArea);
            if (share <= 0.0f) { continue; }
            covered += share;
            Vector2 velocity = ConvertUnits.ToDisplayUnits(sub.Velocity);
            field.Add(velocity.X, velocity.Y, share);
        }
        return covered;
    }

    private void CollectCharacters(Vector2 center, float zoom, int screenWidth, int screenHeight, float screenArea) {
        Span<Character> characters = CollectionsMarshal.AsSpan(Character.CharacterList);
        for (int i = 0; i < characters.Length; i++) {
            Character character = characters[i];
            if (character == null || character.Removed || !character.Enabled || !character.IsVisible) { continue; }
            PhysicsBody collider = character.AnimController?.Collider;
            if (collider == null) { continue; }
            float radius = ConvertUnits.ToDisplayUnits(collider.Height * 0.5f + collider.Radius) * zoom;
            if (radius <= 0.0f) { continue; }
            Vector2 origin = character.WorldPosition;
            float left = (origin.X - center.X) * zoom + screenWidth * 0.5f - radius;
            float top = -(origin.Y - center.Y) * zoom + screenHeight * 0.5f - radius;
            float extent = radius * 2.0f;
            float share = Overlap(left, top, extent, extent, screenWidth, screenHeight, screenArea);
            if (share <= 0.0f) { continue; }
            if (share > MaxCharacterShare) { share = MaxCharacterShare; }
            Vector2 velocity = ConvertUnits.ToDisplayUnits(collider.LinearVelocity);
            Submarine carrier = character.Submarine;
            if (carrier != null) { velocity += ConvertUnits.ToDisplayUnits(carrier.Velocity); }
            field.Add(velocity.X, velocity.Y, share);
        }
    }

    private static float Overlap(float left, float top, float width, float height,
        int screenWidth, int screenHeight, float screenArea) {
        float right = left + width;
        float bottom = top + height;
        if (left < 0.0f) { left = 0.0f; }
        if (top < 0.0f) { top = 0.0f; }
        if (right > screenWidth) { right = screenWidth; }
        if (bottom > screenHeight) { bottom = screenHeight; }
        float spanX = right - left;
        float spanY = bottom - top;
        if (spanX <= 0.0f || spanY <= 0.0f) { return 0.0f; }
        return spanX * spanY / screenArea;
    }
}
