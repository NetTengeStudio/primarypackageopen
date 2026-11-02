using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    private Camera editorCamera;
    private Submarine editorCameraFrame;
    private Vector2 editorCameraLocal, editorCameraReturn;
    private float editorCameraReturnZoom, editorCameraReturnMaxZoom;
    private MouseState editorViewMouse;
    private bool editorPanning;
    private Vector2 EditorCameraOrigin => editorCameraFrame is { Removed: false } ? editorCameraFrame.Position : Vector2.Zero;
    private void BeginEditorViewport() {
        editorCamera = GameMain.GameScreen.Cam;
        editorCameraFrame = Submarine.FindClosest(editorCamera.Position);
        editorCameraLocal = editorCameraReturn = editorCamera.Position - EditorCameraOrigin;
        editorCameraReturnZoom = editorCamera.Zoom; editorCameraReturnMaxZoom = editorCamera.MaxZoom;
        editorCamera.MaxZoom = 10; // same limit as SubEditorScreen's camera
        editorCamera.StopMovement(); editorViewMouse = Mouse.GetState(); editorPanning = false;
    }
    private void EndEditorViewport() {
        if (editorCamera == null) { return; }
        editorCamera.MaxZoom = editorCameraReturnMaxZoom; editorCamera.Zoom = editorCameraReturnZoom;
        editorCamera.Position = editorCameraReturn + EditorCameraOrigin;
        editorCamera.StopMovement(); editorCamera.UpdateTransform(false);
        editorCamera = null; editorCameraFrame = null; editorPanning = false;
    }
    private void MoveEditorCamera(Camera camera, float deltaTime) {
        if (camera != editorCamera || !float.IsFinite(deltaTime) || deltaTime < 0) { return; }
        if (editorCameraFrame?.Removed == true) { editorCameraLocal = camera.Position; editorCameraReturn += editorCameraFrame.Position; editorCameraFrame = null; }
        camera.Position = editorCameraLocal + EditorCameraOrigin;
        camera.UpdateTransform(false, updateListener: false);
        MouseState mouse = Mouse.GetState();
        bool input = GUIRuntime.Instance.ViewportInputAvailable;
        bool middle = mouse.MiddleButton == ButtonState.Pressed;
        if (!input || !middle) { editorPanning = false; }
        else if (editorViewMouse.MiddleButton == ButtonState.Released) { editorPanning = !editorDrag && !editorMarquee && !editorArrangement.HasValue; }
        if (input) {
            if (editorPanning) {
                camera.Position += camera.ScreenToWorld(new Vector2(editorViewMouse.X, editorViewMouse.Y)) - camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
                camera.UpdateTransform(false, updateListener: false);
            }
            int wheel = mouse.ScrollWheelValue - editorViewMouse.ScrollWheelValue;
            if (wheel != 0) {
                Vector2 pointer = new(mouse.X, mouse.Y), anchor = camera.ScreenToWorld(pointer);
                // Match native free-camera wheel sensitivity, anchored under the pointer.
                camera.Zoom += wheel / 1000f * camera.Zoom;
                camera.UpdateTransform(false, updateListener: false);
                camera.Position += anchor - camera.ScreenToWorld(pointer);
            }
            KeyboardState keys = Keyboard.GetState(); Vector2 direction = Vector2.Zero;
            if (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left)) { direction.X--; }
            if (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right)) { direction.X++; }
            if (keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up)) { direction.Y++; }
            if (keys.IsKeyDown(Keys.S) || keys.IsKeyDown(Keys.Down)) { direction.Y--; }
            if (direction.LengthSquared() > 1) { direction.Normalize(); }
            float speed = camera.Resolution.Y / camera.Zoom; // one viewport height per second
            if (keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift)) { speed *= 2; }
            camera.Position += direction * (speed * deltaTime);
        }
        camera.Position = Vector2.Clamp(camera.Position, new Vector2(-10000000), new Vector2(10000000));
        editorCameraLocal = camera.Position - EditorCameraOrigin; editorViewMouse = mouse;
        camera.UpdateTransform(false);
    }
}
