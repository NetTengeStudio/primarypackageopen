using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;

// Exercise installed engine methods with synthetic input. No GameMain instance,
// game loop, window, SDL input polling or network peer is started.
internal static partial class Program
{
    private sealed class Context(string game, string server) : AssemblyLoadContext
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "0Harmony") { return typeof(Harmony).Assembly; }
            if (name.Name!.StartsWith("System.", StringComparison.Ordinal) || name.Name is "System" or "netstandard" or "mscorlib") { return null; }
            string path = Path.Combine(name.Name == "DedicatedServer" ? server : game, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static int checks;
    private static void Check(bool pass, string name)
    {
        if (!pass) { throw new InvalidOperationException(name); }
        checks++;
        Console.WriteLine("PASS " + name);
    }
    private static object Blank(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo Method(Type type, string name, params Type[] parameters) => AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
    private static bool AllowInput(ref bool __result) { __result = true; return false; }
    private static int earlyHoverReads;
    private static bool CountHoverRead() { earlyHoverReads++; return false; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailingWorldUpdate() => throw new InvalidOperationException("synthetic world failure");

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("InputContracts <game> <client-mod.dll> <server-mod.dll> <server>"); return 2; }
        try
        {
            Client(args[0], args[1]);
            Server(args[0], args[2], args[3]);
            Console.WriteLine($"InputContracts: {checks} checks, 0 failures; installed engine, synthetic input, no game loop");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Client(string game, string modPath)
    {
        var context = new Context(game, game);
        Assembly engine = context.LoadFromAssemblyPath(Path.Combine(game, "Barotrauma.dll"));
        Assembly mod = context.LoadFromAssemblyPath(Path.GetFullPath(modPath));
        Type Native(string name) => engine.GetType("Barotrauma." + name, true)!;
        Type Patch(string name) => mod.GetType("PrimaryPackage.GUI." + name, true)!;
        var harmony = new Harmony("primary.input.regression.client");
        Type player = Native("PlayerInput"), gui = Native("GUI"), key = Native("Key"), inputType = Native("InputType");
        Type runtimeType = Patch("GUIRuntime"), input = Patch("GUIInput");
        object runtime = Blank(runtimeType), capture = Blank(input);
        Field(runtimeType, "<Input>k__BackingField").SetValue(runtime, capture);
        Field(runtimeType, "<Windows>k__BackingField").SetValue(runtime, Activator.CreateInstance(Patch("GUIWindowManager")));
        foreach (string list in new[] { "overlays", "nativeSurfaces" })
        { FieldInfo field = Field(runtimeType, list); field.SetValue(runtime, Activator.CreateInstance(field.FieldType)); }
        Field(runtimeType, "<Instance>k__BackingField").SetValue(null, runtime);
        FieldInfo blocked = Field(input, "<BlocksWorld>k__BackingField");
        PropertyInfo dispatcher = AccessTools.Property(gui, "KeyboardDispatcher");
        dispatcher.SetValue(null, Blank(dispatcher.PropertyType));
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(Native("GameMain"), "WindowActive"), prefix: new HarmonyMethod(Method(typeof(Program), nameof(AllowInput), typeof(bool).MakeByRefType())));
            Field(player, "allowInput").SetValue(null, true);
            foreach (string patch in new[] { "GUIWorldMouse", "GUIWorldScroll", "GUICharacterInteraction", "GUICharacterUI" })
            { harmony.CreateClassProcessor(Patch(patch)).Patch(); }
            // Install legacy raw keyboard patches too when checking a baseline.
            foreach (string patch in new[] { "GUIWorldKeys", "GUIWorldKeyboard", "GUIWorldInputScope", "GUICharacterKeys" })
            { if (mod.GetType("PrimaryPackage.GUI." + patch) is Type type) { harmony.CreateClassProcessor(type).Patch(); } }

            Assembly framework = context.LoadFromAssemblyName(new AssemblyName("MonoGame.Framework.Windows.NetStandard"));
            Type mouse = framework.GetType("Microsoft.Xna.Framework.Input.MouseState", true)!;
            Type button = framework.GetType("Microsoft.Xna.Framework.Input.ButtonState", true)!;
            Type keyboard = framework.GetType("Microsoft.Xna.Framework.Input.KeyboardState", true)!;
            Type keys = framework.GetType("Microsoft.Xna.Framework.Input.Keys", true)!;
            object released = Enum.Parse(button, "Released"), pressed = Enum.Parse(button, "Pressed");
            object Mouse(bool down, int wheel) => Activator.CreateInstance(mouse, 10, 10, wheel, down ? pressed : released, released, released, released, released)!;
            Field(player, "oldMouseState").SetValue(null, Mouse(true, 0));
            Field(player, "mouseState").SetValue(null, Mouse(false, 120));
            Array heldKeys = Array.CreateInstance(keys, 2);
            heldKeys.SetValue(Enum.Parse(keys, "W"), 0); heldKeys.SetValue(Enum.Parse(keys, "Escape"), 1);
            Field(player, "keyboardState").SetValue(null, Activator.CreateInstance(keyboard, new object[] { heldKeys }));
            Field(player, "oldKeyboardState").SetValue(null, Activator.CreateInstance(keyboard));
            blocked.SetValue(capture, true);
            Check(Method(player, "PrimaryMouseButtonClicked").Invoke(null, null) is true, "native button release remains readable while custom GUI captures the world");
            Check(AccessTools.Property(player, "ScrollWheelSpeed").GetValue(null) is 120, "native menus retain wheel input during custom capture");
            Check(Method(player, "KeyHit", keys).Invoke(null, new[] { Enum.Parse(keys, "Escape") }) is true, "native menu Escape remains readable during custom capture");
            VerifyPersistentMenuCapture(mod, Native, Patch, harmony, runtime);

            Type scope = Patch("GUIWorldInputScope");
            MethodInfo enter = Method(scope, "Prefix", typeof(int).MakeByRefType()), leave = Method(scope, "Finalizer", typeof(int));
            object?[] outer = { 0 }, inner = { 0 };
            enter.Invoke(null, outer);
            try
            {
                Check(Method(player, "PrimaryMouseButtonClicked").Invoke(null, null) is false, "world consumer cannot click through custom GUI");
                Check(AccessTools.Property(player, "ScrollWheelSpeed").GetValue(null) is 0, "world consumer cannot scroll through custom GUI");
                enter.Invoke(null, inner); leave.Invoke(null, inner);
                Check(AccessTools.Property(scope, "Active").GetValue(null) is true, "nested world updates retain outer capture");
            }
            finally { leave.Invoke(null, outer); }
            Check(Method(player, "PrimaryMouseButtonClicked").Invoke(null, null) is true, "world finalizer restores raw pointer for subsequent native widgets");
            Check(Harmony.GetPatchInfo(Method(Native("SubEditorScreen"), "Update", typeof(double))).Owners.Contains(harmony.Id), "installed editor Update owns a scoped pointer capture");
            MethodInfo failing = Method(typeof(Program), nameof(FailingWorldUpdate));
            harmony.Patch(failing, prefix: new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave));
            try { failing.Invoke(null, null); throw new InvalidOperationException("synthetic exception was swallowed"); }
            catch (TargetInvocationException error) when (error.InnerException?.Message == "synthetic world failure") { }
            Check(AccessTools.Property(scope, "Active").GetValue(null) is false, "Harmony finalizer releases world capture after an exception");

            Type settings = Native("GameSettings");
            FieldInfo config = Field(settings, "currentConfig");
            object settingsValue = Activator.CreateInstance(config.FieldType)!;
            FieldInfo map = Field(config.FieldType, "KeyMap");
            // Use the engine's QWERTY mapping directly: layout conversion requires
            // SDL initialization and is outside this offline input regression.
            object keyMap = Activator.CreateInstance(map.FieldType)!;
            Field(map.FieldType, "Bindings").SetValue(keyMap, Field(map.FieldType, "DefaultsQwerty").GetValue(null));
            map.SetValue(settingsValue, keyMap); config.SetValue(null, settingsValue);
            object movement = Activator.CreateInstance(key, Enum.Parse(inputType, "Up"))!;
            MethodInfo setState = Method(key, "SetState");
            blocked.SetValue(capture, false);
            object nativeHover = Blank(Native("GUIFrame"));
            Method(gui, "ForceMouseOn", Native("GUIComponent")).Invoke(null, new[] { nativeHover });
            setState.Invoke(movement, null);
            Check(AccessTools.Property(key, "Held").GetValue(movement) is true, "W movement survives native chat or HUD hover");
            blocked.SetValue(capture, true); setState.Invoke(movement, null);
            Check(AccessTools.Property(key, "Held").GetValue(movement) is false, "custom window captures movement at the semantic character-key layer");
            blocked.SetValue(capture, false); setState.Invoke(movement, null);
            Check(AccessTools.Property(key, "Held").GetValue(movement) is true, "closing custom capture immediately restores character movement");
            Field(player, "mouseState").SetValue(null, Mouse(true, 120));
            object voice = Activator.CreateInstance(key, Enum.Parse(inputType, "Voice"))!;
            object voiceBinding = Activator.CreateInstance(Native("KeyOrMouse"), Enum.Parse(Native("MouseButton"), "PrimaryMouse"))!;
            object bindings = Field(map.FieldType, "Bindings").GetValue(keyMap)!;
            bindings = bindings.GetType().GetMethod("SetItem")!.Invoke(bindings, new[] { Enum.Parse(inputType, "Voice"), voiceBinding })!;
            Field(map.FieldType, "Bindings").SetValue(keyMap, bindings); map.SetValue(settingsValue, keyMap); config.SetValue(null, settingsValue);
            blocked.SetValue(capture, true); enter.Invoke(null, outer);
            try
            {
                setState.Invoke(voice, null);
                Check(AccessTools.Property(key, "Held").GetValue(voice) is true, "voice rebound to a mouse button survives world pointer capture");
                Check(AccessTools.Property(scope, "Active").GetValue(null) is true, "semantic key finalizer restores the outer world scope");
            }
            finally { leave.Invoke(null, outer); }
            blocked.SetValue(capture, false);
            object overlay = Blank(Patch("GUIOverlay"));
            Field(overlay.GetType(), "Visible").SetValue(overlay, (Func<bool>)(() => true));
            Field(overlay.GetType(), "BlocksWorld").SetValue(overlay, true);
            object overlays = Field(runtimeType, "overlays").GetValue(runtime)!;
            overlays.GetType().GetMethod("Add")!.Invoke(overlays, new[] { overlay });
            bindings = bindings.GetType().GetMethod("SetItem")!.Invoke(bindings, new[] { Enum.Parse(inputType, "Up"), voiceBinding })!;
            Field(map.FieldType, "Bindings").SetValue(keyMap, bindings); map.SetValue(settingsValue, keyMap); config.SetValue(null, settingsValue);
            enter.Invoke(null, outer);
            try
            {
                setState.Invoke(movement, null);
                Check(AccessTools.Property(key, "Held").GetValue(movement) is true, "pointer-only overlay preserves movement rebound to a mouse button");
                Field(overlay.GetType(), "CapturesMovement").SetValue(overlay, true); setState.Invoke(movement, null);
                Check(AccessTools.Property(key, "Held").GetValue(movement) is false, "overlay with explicit movement ownership captures the rebound action");
            }
            finally { leave.Invoke(null, outer); overlays.GetType().GetMethod("Clear")!.Invoke(overlays, null); }
            object character = Blank(Native("Character")); Field(Native("Character"), "controlled").SetValue(null, character);
            Check(Method(Patch("GUICharacterInteraction"), "Prefix", Native("Character")).Invoke(null, new[] { character }) is true, "native hover preserves ladder and interaction update");
            ViewContracts(context, engine, mod, harmony, capture);
        }
        finally { harmony.UnpatchSelf(); Field(runtimeType, "<Instance>k__BackingField").SetValue(null, null); }
    }

    private static void VerifyPersistentMenuCapture(Assembly mod, Func<string, Type> native, Func<string, Type> patch, Harmony harmony, object runtime)
    {
        Type gui = native("GUI"), bridge = patch("GUIBridge"), runtimeType = patch("GUIRuntime");
        FieldInfo instance = Field(runtimeType, "<Instance>k__BackingField");
        object button = Blank(native("GUIButton"));
        AccessTools.Property(button.GetType(), "Visible").SetValue(button, true);
        MethodInfo force = Method(gui, "ForceMouseOn", native("GUIComponent"));
        PropertyInfo mouseOn = AccessTools.Property(gui, "MouseOn"), pause = AccessTools.Property(gui, "PauseMenuOpen");
        MethodInfo hover = Method(gui, "UpdateMouseOn");
        MethodInfo validator = Method(bridge, "ValidateMouseCapture");
        object updateSet = Field(gui, "updateListSet").GetValue(null)!;
        MethodInfo add = updateSet.GetType().GetMethod("Add")!, remove = updateSet.GetType().GetMethod("Remove")!;
        object? previous = mouseOn.GetValue(null);
        harmony.Patch(hover, prefix: new HarmonyMethod(Method(typeof(Program), nameof(CountHoverRead))));
        try
        {
            instance.SetValue(null, null);
            pause.SetValue(null, true);
            // This is the point in GameMain.Update before HandlePersistingElements:
            // the settings button was hovered last frame but is absent from the world-only list.
            remove.Invoke(updateSet, new[] { button });
            force.Invoke(null, new[] { button });
            earlyHoverReads = 0;
            Method(bridge, "UpdateEditorInput").Invoke(null, null);
            Check(earlyHoverReads == 0 && ReferenceEquals(mouseOn.GetValue(null), button),
                "Escape menu retains its settings button capture before persistent GUI elements are appended");
            add.Invoke(updateSet, new[] { button });
            validator.Invoke(null, null);
            Check(ReferenceEquals(mouseOn.GetValue(null), button), "settings button keeps native capture in the complete GUI list");
            remove.Invoke(updateSet, new[] { button }); pause.SetValue(null, false);
            validator.Invoke(null, null);
            Check(mouseOn.GetValue(null) == null, "closed pause menu releases its old button capture");
            Method(bridge, "UpdateEditorInput").Invoke(null, null);
            Check(earlyHoverReads == 1, "world hover refresh resumes after the pause menu closes");
        }
        finally
        {
            harmony.Unpatch(hover, Method(typeof(Program), nameof(CountHoverRead)));
            remove.Invoke(updateSet, new[] { button }); pause.SetValue(null, false);
            force.Invoke(null, new[] { previous }); instance.SetValue(null, runtime);
        }
    }

    private static void Server(string game, string modPath, string serverRoot)
    {
        var context = new Context(game, serverRoot);
        Assembly engine = context.LoadFromAssemblyPath(Path.Combine(serverRoot, "DedicatedServer.dll"));
        Assembly mod = context.LoadFromAssemblyPath(Path.GetFullPath(modPath));
        Type Native(string name) => engine.GetType("Barotrauma.Networking." + name, true)!;
        Type server = Native("GameServer"), client = Native("Client"), packet = Native("PeerDisconnectPacket");
        object host = Blank(server), owner = Blank(client), connection = Blank(Native("SteamP2PConnection"));
        AccessTools.Property(server, "OwnerConnection").SetValue(host, connection);
        AccessTools.Property(client, "Connection").SetValue(owner, connection);
        AccessTools.Property(client, "InGame").SetValue(owner, true);
        object clients = Field(server, "connectedClients").FieldType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        clients.GetType().GetMethod("Add")!.Invoke(clients, new[] { owner }); Field(server, "connectedClients").SetValue(host, clients);
        Type recovery = mod.GetType("INet.SyncRecovery", true)!;
        var harmony = new Harmony("primary.input.regression.server");
        MethodInfo disconnect = Method(server, "DisconnectClient", client, packet);
        harmony.Patch(disconnect, prefix: new HarmonyMethod(AccessTools.Method(recovery, "VetoDesyncDisconnect")));
        try
        {
            Type reason = Native("DisconnectReason");
            foreach (string name in new[] { "ExcessiveDesyncOldEvent", "ExcessiveDesyncRemovedEvent", "SyncTimeout" })
            {
                object message = Method(packet, "WithReason", reason).Invoke(null, new[] { Enum.Parse(reason, name) })!;
                disconnect.Invoke(host, new[] { owner, message });
                Check(AccessTools.Property(client, "InGame").GetValue(owner) is true && (int)AccessTools.Property(clients.GetType(), "Count").GetValue(clients)! == 1,
                    "owner " + name + " rejection preserves native client membership before disconnect mutates it");
            }
            object remote = Blank(client);
            AccessTools.Property(client, "Connection").SetValue(remote, Blank(Native("SteamP2PConnection")));
            clients.GetType().GetMethod("Add")!.Invoke(clients, new[] { remote });
            Type timing = engine.GetType("Barotrauma.Timing", true)!;
            FieldInfo time = Field(timing, "TotalTime");
            time.SetValue(null, 100d);
            Method(recovery, "Reset").Invoke(null, null);
            Method(recovery, "SampleProgress", clients.GetType()).Invoke(null, new[] { clients });
            MethodInfo veto = AccessTools.Method(recovery, "VetoDesyncDisconnect");
            bool Allowed(string name) => (bool)veto.Invoke(null, new[] { host, remote, Method(packet, "WithReason", reason).Invoke(null, new[] { Enum.Parse(reason, name) }) })!;
            Check(!Allowed("ExcessiveDesyncOldEvent"), "progressing remote client receives grace for an old retained event");
            Check(Allowed("ExcessiveDesyncRemovedEvent"), "missing event cannot be hidden by recovery grace");
            time.SetValue(null, 111d);
            Check(Allowed("ExcessiveDesyncOldEvent"), "remote client with no progress exhausts recovery grace");
            Method(recovery, "Reset").Invoke(null, null);
        }
        finally { harmony.UnpatchSelf(); }
    }
}
