using System.Reflection;
using HarmonyLib;

internal static partial class Program
{
    private static void ViewContracts(Context context, Assembly engine, Assembly mod, Harmony harmony, object capture)
    {
        Type Native(string name) => engine.GetType("Barotrauma." + name, true)!;
        Type Patch(string name) => mod.GetType("PrimaryPackage.GUI." + name, true)!;
        Assembly framework = context.LoadFromAssemblyName(new AssemblyName("MonoGame.Framework.Windows.NetStandard"));
        Type vector = AccessTools.Property(Native("Camera"), "TargetPos").PropertyType;
        Type point = AccessTools.Property(Native("Camera"), "Resolution").PropertyType;
        Type mouse = framework.GetType("Microsoft.Xna.Framework.Input.MouseState", true)!;
        Type button = framework.GetType("Microsoft.Xna.Framework.Input.ButtonState", true)!;
        Type cameraType = Native("Camera"), gui = Native("GUI"), player = Native("PlayerInput");
        object Vec(float x, float y) => Activator.CreateInstance(vector, x, y)!;
        void Set(object instance, string property, object value) => AccessTools.Property(instance.GetType(), property).SetValue(instance, value);
        object Get(object instance, string property) => AccessTools.Property(instance.GetType(), property).GetValue(instance)!;
        float Number(object value, string field) => (float)Field(value.GetType(), field).GetValue(value)!;
        bool At(object value, float x, float y) => Math.Abs(Number(value, "X") - x) < .001f && Math.Abs(Number(value, "Y") - y) < .001f;
        object Mouse(int x, int y, bool held) => Activator.CreateInstance(mouse, x, y, 0, Enum.Parse(button, "Released"),
            Enum.Parse(button, "Released"), Enum.Parse(button, held ? "Pressed" : "Released"), Enum.Parse(button, "Released"), Enum.Parse(button, "Released"))!;
        void Pointer(int x, int y, bool held = false) => Field(player, "mouseState").SetValue(null, Mouse(x, y, held));
        object Camera()
        {
            object value = Blank(cameraType);
            Set(value, "Resolution", Activator.CreateInstance(point, 1920, 1080)!);
            Set(value, "MoveSmoothness", 1f); Set(value, "ZoomSmoothness", 8f);
            Set(value, "MinZoom", .1f); Set(value, "MaxZoom", 6f);
            Field(cameraType, "zoom").SetValue(value, 1.2f);
            Set(value, "TargetPos", Vec(100, 200)); Set(value, "Position", Vec(280, 290));
            Set(value, "OffsetAmount", 500f); Field(cameraType, "previousOffset").SetValue(value, Vec(180, 90));
            return value;
        }
        object camera = Camera(), gameScreen = Blank(Native("GameScreen"));
        Field(gameScreen.GetType(), "cam").SetValue(gameScreen, camera);
        Field(Native("GameMain"), "GameScreen").SetValue(null, gameScreen);
        PropertyInfo selected = AccessTools.Property(Native("Screen"), "Selected");
        object? oldScreen = selected.GetValue(null);
        selected.SetValue(null, gameScreen);
        FieldInfo blocked = Field(capture.GetType(), "<BlocksWorld>k__BackingField");
        MethodInfo force = Method(gui, "ForceMouseOn", Native("GUIComponent"));
        object nativeHover = Blank(Native("GUIFrame"));
        MethodInfo move = Method(cameraType, "MoveCamera", typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(bool?));
        void Move() => move.Invoke(camera, new object?[] { 1f / 60, true, true, true, false });
        harmony.CreateClassProcessor(Patch("GUICameraInput")).Patch();
        harmony.CreateClassProcessor(Patch("GUICharacterCursor")).Patch();
        // Old builds install the recentering postfix here as production did.
        if (mod.GetType("PrimaryPackage.GUI.GUICharacterControlLocal") is Type legacy) { harmony.CreateClassProcessor(legacy).Patch(); }
        try
        {
            blocked.SetValue(capture, false); force.Invoke(null, new[] { nativeHover });
            Pointer(10, 10); Move();
            Check(At(Get(camera, "Position"), 280, 290), "native HUD hover retains the actual follow-camera offset instead of recentering");
            Check(Get(camera, "Zoom") is float zoom && Math.Abs(zoom - 1.2f) < .000001f, "native HUD hover retains actual camera zoom");
            Check(Get(camera, "OffsetAmount") is 500f, "GUI capture does not overwrite the engine offset amount");
            Pointer(960, 540); Move();
            Check(At(Get(camera, "Position"), 280, 290), "GUI at the exact screen centre retains the previous view");
            Check(At(Field(cameraType, "previousOffset").GetValue(camera)!, 180, 90), "hover leaves the engine retained offset intact");
            Set(camera, "TargetPos", Vec(140, 220)); Move();
            Check(At(Get(camera, "Position"), 320, 310), "camera continues to follow a moving target while its view offset is frozen");
            force.Invoke(null, new object?[] { null }); blocked.SetValue(capture, true);
            Pointer(10, 10); Move();
            Check(At(Get(camera, "Position"), 320, 310), "custom GUI capture preserves the same view as native HUD hover");
            Set(camera, "OffsetAmount", 0f); Move();
            Check(At(Get(camera, "Position"), 320, 310), "frozen view survives a zero offset amount without losing retained engine state");
            Set(camera, "OffsetAmount", 500f);
            blocked.SetValue(capture, false); Move();
            Check(!At(Get(camera, "Position"), 320, 310), "leaving GUI resumes native pointer-driven camera movement");

            Set(camera, "TargetPos", Vec(0, 0));
            object freePosition = Get(camera, "Position"); force.Invoke(null, new[] { nativeHover }); Move();
            Check(Equals(Get(camera, "Position"), freePosition), "hover does not move a free camera");

            object character = Field(Native("Character"), "controlled").GetValue(null)!;
            Set(character, "CursorPosition", Vec(1300, -700));
            Method(Native("Character"), "UpdateLocalCursor", cameraType).Invoke(character, new[] { camera });
            Check(At(Get(character, "CursorPosition"), 1300, -700), "HUD hover preserves the existing character aim point");
            Set(character, "CursorPosition", Vec(-800, 450));
            blocked.SetValue(capture, true); force.Invoke(null, new object?[] { null });
            Method(Native("Character"), "UpdateLocalCursor", cameraType).Invoke(character, new[] { camera });
            Check(At(Get(character, "CursorPosition"), -800, 450), "custom window preserves aim on the opposite side of the character");

            // Construct only the native health/inventory facts needed for gesture
            // eligibility. No character, physics or UI update loop is run.
            object inventory = Blank(Native("CharacterInventory")), health = Blank(Native("CharacterHealth"));
            FieldInfo slots = Field(inventory.GetType(), "slotsByType"); slots.SetValue(inventory, Activator.CreateInstance(slots.FieldType));
            Set(character, "Inventory", inventory); Set(character, "CharacterHealth", health);
            Field(health.GetType(), "Character").SetValue(health, character); Field(health.GetType(), "vitality").SetValue(health, 100f);
            Type immersion = mod.GetType("PrimaryPackage.ZoomKey.ImmersionZoom", true)!;
            MethodInfo update = Method(immersion, "Update", cameraType, typeof(float));
            object lookCamera = Camera();
            blocked.SetValue(capture, false); force.Invoke(null, new object?[] { null }); Pointer(1700, 300, true);
            update.Invoke(null, new[] { lookCamera, (object)1f }); update.Invoke(null, new[] { lookCamera, (object)1f });
            object states = Field(immersion, "States").GetValue(null)!;
            object state = states.GetType().GetMethod("GetOrCreateValue")!.Invoke(states, new[] { lookCamera })!;
            float charge = Number(state, "Charge"), factor = Number(state, "Factor");
            Check(charge == 1f && factor > 0f, "native eligible empty-handed character engages the look gesture before hover");
            force.Invoke(null, new[] { nativeHover }); Pointer(10, 10, true);
            for (int i = 0; i < 30; i++) { update.Invoke(null, new[] { lookCamera, (object)(1f / 60) }); }
            Check(Number(state, "Charge") == charge && Math.Abs(Number(state, "Factor") - factor) < .000001f,
                "hover pauses an existing held look gesture without resetting its charge or zoom factor");
            force.Invoke(null, new object?[] { null }); update.Invoke(null, new[] { lookCamera, (object)(1f / 60) });
            Check(Number(state, "Charge") == 1f && Number(state, "Factor") >= factor, "leaving HUD resumes the held look gesture without recharging");
            Pointer(10, 10); update.Invoke(null, new[] { lookCamera, (object)(1f / 60) });
            Check(Field(state.GetType(), "Engaged").GetValue(state) is false, "releasing the look button still disengages the gesture");

            object idleCamera = Camera(); force.Invoke(null, new[] { nativeHover }); Pointer(10, 10, true);
            update.Invoke(null, new[] { idleCamera, (object)1f });
            force.Invoke(null, new object?[] { null }); update.Invoke(null, new[] { idleCamera, (object)1f });
            object idle = states.GetType().GetMethod("GetOrCreateValue")!.Invoke(states, new[] { idleCamera })!;
            Check(Number(idle, "Charge") == 0f, "a look-button press owned by GUI cannot start a world look gesture on exit");
        }
        finally { selected.SetValue(null, oldScreen); blocked.SetValue(capture, false); force.Invoke(null, new object?[] { null }); }
    }
}
