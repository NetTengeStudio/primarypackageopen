using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

[assembly: IgnoresAccessChecksTo("Barotrauma")]
[assembly: IgnoresAccessChecksTo("DedicatedServer")]
[assembly: IgnoresAccessChecksTo("BarotraumaCore")]

// Loaded only by DOTNET_STARTUP_HOOKS in isolated test processes.
public static class StartupHook
{
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly string root = Environment.GetEnvironmentVariable("PP_ONLINE_ROOT");
    static readonly string role = Environment.GetEnvironmentVariable("PP_ONLINE_ROLE");
    static long nextPoll, nextHeartbeat;
    static bool done, captured, pasteCaptured;
#if SERVER
    static bool started, fixtureQueued, rejoinIssued;
    delegate void Stamp(ReadOnlySpan<ushort> ids, byte actor, ulong revision);
#else
    static bool editorOpened, editSent, pasteSent, deleteSent, staleInjected;
    static Type editorType;
    static byte[] originalXml;
    delegate byte[] SaveEntities(IReadOnlyList<MapEntity> entities);
    delegate List<MapEntity> LoadEntities(ReadOnlySpan<byte> bytes, Submarine owner);
#endif
    static Item fixture;
    static readonly Harmony harmony = new("primary.online.editor.acceptance");

    public static void Initialize()
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(role)) { return; }
        if (role is not ("server" or "a" or "b")) { throw new ArgumentException("Invalid test process role"); }
        // The binaries otherwise reset cwd to the installation and overwrite the
        // operator's settings. All test settings stay in the process's own box.
        harmony.Patch(AccessTools.Method(typeof(System.IO.Directory), nameof(System.IO.Directory.SetCurrentDirectory)),
            prefix: new HarmonyMethod(typeof(StartupHook), nameof(KeepDirectory)));
        harmony.Patch(AccessTools.Method(typeof(Barotrauma.IO.Directory), nameof(Barotrauma.IO.Directory.SetCurrentDirectory)),
            prefix: new HarmonyMethod(typeof(StartupHook), nameof(KeepDirectory)));
#if SERVER
        MethodInfo update = AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.Update));
#else
        MethodInfo update = AccessTools.Method(typeof(GameMain), "Update", new[] { typeof(GameTime) });
#endif
        harmony.Patch(update, postfix: new HarmonyMethod(typeof(StartupHook), nameof(Tick)));
        Log("armed");
    }

    static bool KeepDirectory() => false;
    static string PathOf(string name) => Path.Combine(root, name);
    static void Log(string message) => File.AppendAllText(PathOf(role + ".log"), $"{clock.Elapsed.TotalSeconds:F3} {message}\n");

    static void Tick()
    {
        if (done || clock.ElapsedMilliseconds < nextPoll) { return; }
        nextPoll = clock.ElapsedMilliseconds + 200;
        try
        {
            if (File.Exists(PathOf("stop.flag"))) { Finish(); return; }
            if (clock.Elapsed.TotalSeconds > 300) { throw new TimeoutException("Online editor acceptance timed out"); }
            if (clock.ElapsedMilliseconds >= nextHeartbeat)
            {
                nextHeartbeat = clock.ElapsedMilliseconds + 10000;
                Log($"heartbeat screen={Screen.Selected?.GetType().Name} items={Item.ItemList.Count}");
            }
#if SERVER
            ServerTick();
#else
            ClientTick();
#endif
        }
        catch (Exception error)
        {
            Log(error.ToString());
            File.WriteAllText(PathOf(role + ".failed"), error.ToString());
            Finish();
        }
    }

#if SERVER
    static void ServerTick()
    {
        GameServer server = GameMain.Server;
        if (server == null) { return; }
        if (!started && server.ConnectedClients.Count >= 2)
        {
            foreach (Client client in server.ConnectedClients)
            {
                client.SetPermissions(ClientPermissions.All, DebugConsole.Commands);
                AccessTools.Method(typeof(GameServer), "SendClientPermissions").Invoke(server, new object[] { client, client });
            }
            SubmarineInfo submarine = SubmarineInfo.SavedSubmarines.First(s => s.Name == "-COL- Shooting Range");
            GameMain.NetLobbyScreen.SelectedSub = submarine;
            GameMain.NetLobbyScreen.SelectedModeIdentifier = "sandbox".ToIdentifier();
            GameMain.NetLobbyScreen.LevelSeed = "primary-online-editor";
            server.ServerSettings.BotCount = 0;
            started = server.TryStartGame() == GameServer.TryStartGameResult.Success;
            Log("TryStartGame=" + started);
        }
        if (!server.GameStarted || GameMain.GameSession is not { IsRunning: true }) { return; }
        if (server.EndRoundTimer > 5f) { server.EndRoundTimer = 1f; }
        if (!fixtureQueued && File.Exists(PathOf("a.ready")) && File.Exists(PathOf("b.ready")))
        {
            fixtureQueued = true;
            ItemPrefab prefab = ItemPrefab.Prefabs["circuitbox".ToIdentifier()];
            Entity.Spawner.AddItemToSpawnQueue(prefab, Hull.HullList.First(h => h.Submarine == Submarine.MainSub).WorldPosition,
                onSpawned: item => { fixture = item; File.WriteAllText(PathOf("fixture.id"), item.ID.ToString()); Log("fixture=" + item.ID); });
            Entity.Spawner.AddItemToSpawnQueue(ItemPrefab.Prefabs["andcomponent".ToIdentifier()], Submarine.MainSub.WorldPosition,
                onSpawned: item => File.WriteAllText(PathOf("survivor.id"), item.ID.ToString()));
        }
        if (fixture == null) { return; }
        CheckClipboardAndDeletion();
        if (!File.Exists(PathOf("edit.flag")) && File.Exists(PathOf("a.fixture")) && File.Exists(PathOf("b.fixture")))
        { File.WriteAllText(PathOf("edit.flag"), "start"); }
        if (File.Exists(PathOf("capture.flag"))) { Capture(fixture); }
    }
#else
    static void ClientTick()
    {
        GameMain game = GameMain.Instance;
        if (game == null) { return; }
        game.InactiveSleepTime = TimeSpan.Zero;
        var title = GameMain.TitleScreen;
        if (title != null)
        {
            title.WaitForLanguageSelection = false;
            if (title.LoadState >= 100f && !title.PlayingSplashScreen)
            { AccessTools.Field(typeof(GameMain), "loadingScreenOpen").SetValue(game, false); }
        }
        if (!game.HasLoaded || GameMain.GameSession is not { IsRunning: true }) { return; }
        editorType ??= AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("PrimaryPackage.SubEditor.SubEditorClient")).FirstOrDefault(t => t != null);
        if (editorType == null) { throw new InvalidOperationException("SubEditor client assembly was not loaded by LuaCs"); }
        string state = editorType.GetProperty("State").GetValue(null).ToString();
        if (state != "Live") { return; }
        CheckClipboardAndDeletion();
        if (!editorOpened)
        {
            object editor = AccessTools.Field(editorType, "instance").GetValue(null);
            AccessTools.Method(editorType, "Toggle").Invoke(editor, null);
            if (!(bool)editorType.GetProperty("Editing").GetValue(null)) { throw new InvalidOperationException("Editor did not open with console permission"); }
            editorOpened = true;
            File.WriteAllText(PathOf(role + ".ready"), "live editor open");
            Log("editor open; real native update and INet active");
        }
        if (!File.Exists(PathOf("fixture.id"))) { return; }
        fixture = Entity.FindEntityByID(ushort.Parse(File.ReadAllText(PathOf("fixture.id")))) as Item;
        if (fixture?.GetComponent<CircuitBox>() is not { } box) { return; }
        if (!File.Exists(PathOf(role + ".fixture"))) { File.WriteAllText(PathOf(role + ".fixture"), "native fixture received"); }
        if (!editSent && File.Exists(PathOf("edit.flag")))
        {
            editSent = true;
            box.CreateClientEvent(new CircuitBoxAddLabelEvent(new Vector2(role == "a" ? 32 : 64, 48), Color.White,
                new NetLimitedString("online-" + role), NetLimitedString.Empty));
            CircuitBoxInputOutputNode input = box.InputOutputNodes.First(n => n.NodeType == CircuitBoxInputOutputNode.Type.Input);
            CircuitBoxInputOutputNode output = box.InputOutputNodes.First(n => n.NodeType == CircuitBoxInputOutputNode.Type.Output);
            int port = role == "a" ? 0 : 1;
            CircuitBoxConnection from = input.Connectors[Math.Min(port, input.Connectors.Length - 1)];
            CircuitBoxConnection to = output.Connectors[Math.Min(port, output.Connectors.Length - 1)];
            box.CreateClientEvent(new CircuitBoxClientAddWireEvent(Color.Red,
                CircuitBoxConnectorIdentifier.FromConnection(from), CircuitBoxConnectorIdentifier.FromConnection(to),
                ItemPrefab.Prefabs["redwire".ToIdentifier()].UintIdentifier));
            Log("simultaneous circuit label and wire requests sent through CircuitBridge");
        }
        if (File.Exists(PathOf("capture.flag"))) { Capture(fixture); }
    }
#endif

    static void Capture(Item item)
    {
        if (captured) { return; }
        XElement state = item.GetComponent<CircuitBox>().Save(new XElement("State"));
        File.WriteAllText(PathOf(role + ".native.xml"), state.ToString(SaveOptions.DisableFormatting));
        // RequiredItem is immutable prefab configuration, not replicated graph
        // state. Its error-message attribute exists only in the client build.
        foreach (XElement requirement in state.Elements().Where(e => e.Name.LocalName.Equals("requireditem", StringComparison.OrdinalIgnoreCase)).ToArray())
        { requirement.Remove(); }
        string snapshot = state.ToString(SaveOptions.DisableFormatting);
        File.WriteAllText(PathOf(role + ".circuit.xml"), snapshot);
        File.WriteAllText(PathOf(role + ".done"), "captured");
        captured = true;
    }

    static string CircuitState(Item item)
    {
        XElement state = item.GetComponent<CircuitBox>().Save(new XElement("State"));
        foreach (XElement requirement in state.Elements().Where(e => e.Name.LocalName.Equals("requireditem", StringComparison.OrdinalIgnoreCase)).ToArray())
        { requirement.Remove(); }
        return state.ToString(SaveOptions.DisableFormatting);
    }

    static void CheckClipboardAndDeletion()
    {
        if (!File.Exists(PathOf("fixture.id"))) { return; }
        ushort originalId = ushort.Parse(File.ReadAllText(PathOf("fixture.id")));
        Item original = Entity.FindEntityByID(originalId) as Item;
#if CLIENT
        Type ops = editorType.Assembly.GetType("PrimaryPackage.SubEditor.EntityOps", throwOnError: true);
        if (role == "b" && originalXml == null && original != null)
        { originalXml = AccessTools.Method(ops, "SaveXml").CreateDelegate<SaveEntities>()(new MapEntity[] { original }); }
        if (role == "a" && !pasteSent && File.Exists(PathOf("paste.flag")) && original != null)
        {
            pasteSent = true;
            MapEntity.Copy(new List<MapEntity> { original });
            MapEntity.Paste(original.WorldPosition + new Vector2(128, 0));
            Log("native Copy/Paste submitted through CommandBridge");
        }
#else
        if (!File.Exists(PathOf("clone.id")) && File.Exists(PathOf("paste.flag")))
        {
            foreach (Item item in Item.ItemList)
            {
                if (item.ID == originalId || item.Prefab.Identifier != "circuitbox".ToIdentifier()) { continue; }
                CircuitBox box = item.GetComponent<CircuitBox>();
                if (box == null || !box.Labels.Any(label => label.HeaderText.Value.StartsWith("online-", StringComparison.Ordinal))) { continue; }
                File.WriteAllText(PathOf("clone.id"), item.ID.ToString());
                Log("authoritative paste=" + item.ID);
                break;
            }
        }
#endif
        if (!File.Exists(PathOf("clone.id"))) { return; }
        ushort cloneId = ushort.Parse(File.ReadAllText(PathOf("clone.id")));
        Item clone = Entity.FindEntityByID(cloneId) as Item;
        if (clone != null && !pasteCaptured)
        {
            string graph = CircuitState(clone);
            if (original == null || graph != CircuitState(original)) { throw new InvalidOperationException("Copy/Paste lost the circuit graph"); }
            File.WriteAllText(PathOf(role + ".paste.xml"), graph);
            File.WriteAllText(PathOf(role + ".paste.done"), cloneId.ToString());
            pasteCaptured = true;
        }
#if CLIENT
        if (role == "a" && !deleteSent && File.Exists(PathOf("delete.flag")) && original != null && clone != null)
        {
            deleteSent = true;
            var entities = new List<MapEntity> { original, clone };
            SubEditorScreen.StoreCommand(new AddOrDeleteCommand(entities, true));
            foreach (MapEntity entity in entities) { if (!entity.Removed) { entity.Remove(); } }
            Log("native batch Delete submitted through CommandBridge");
        }
#endif
        if (File.Exists(PathOf("delete.flag")) && Entity.FindEntityByID(originalId) == null && Entity.FindEntityByID(cloneId) == null)
        { File.WriteAllText(PathOf(role + ".deleted"), "both authoritative IDs absent"); }
#if SERVER
        if (!rejoinIssued && File.Exists(PathOf("a.deleted")) && File.Exists(PathOf("b.deleted")) && File.Exists(PathOf("server.deleted")))
        {
            ushort survivorId = ushort.Parse(File.ReadAllText(PathOf("survivor.id")));
            Type server = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("PrimaryPackage.SubEditor.SubEditorServer")).First(t => t != null);
            object instance = AccessTools.Field(server, "instance").GetValue(null);
            object authority = AccessTools.Field(server, "authority").GetValue(instance);
            object versions = authority.GetType().GetProperty("Versions").GetValue(authority);
            ulong revision = (ulong)authority.GetType().GetProperty("Rev").GetValue(authority);
            // Make the live, loose item part of the authoritative InPlace
            // snapshot. Its old container is already stamped Gone by Delete.
            AccessTools.Method(versions.GetType(), "Stamp").CreateDelegate<Stamp>(versions)(new[] { survivorId }, 0, revision);
            rejoinIssued = true;
            File.WriteAllText(PathOf("rejoin.flag"), "inject stale containment and request snapshot");
        }
#else
        if (role == "b" && File.Exists(PathOf("rejoin.flag")))
        {
            ushort survivorId = ushort.Parse(File.ReadAllText(PathOf("survivor.id")));
            Item survivor = Entity.FindEntityByID(survivorId) as Item;
            if (survivor == null) { throw new InvalidOperationException("Live snapshot survivor is missing"); }
            if (!staleInjected)
            {
                if (originalXml == null) { throw new InvalidOperationException("No old container image was captured"); }
                Item stale = AccessTools.Method(ops, "LoadExact").CreateDelegate<LoadEntities>()(originalXml, Submarine.MainSub)?.Single() as Item;
                if (stale == null) { throw new InvalidOperationException("Could not construct stale client container"); }
                stale.GetComponent<ItemContainer>().Inventory.ForceToSlot(survivor, 0);
                staleInjected = true;
                object editor = AccessTools.Field(editorType, "instance").GetValue(null);
                AccessTools.Method(editorType, "Join").Invoke(editor, new object[] { false });
                Log("requested real snapshot with Gone container and live child in stale local inventory");
            }
            else if (editorType.GetProperty("State").GetValue(null).ToString() == "Live")
            {
                if (Entity.FindEntityByID(originalId) != null || survivor.Removed || survivor.ParentInventory != null)
                { throw new InvalidOperationException("Snapshot failed to preserve the authoritative survivor"); }
                File.WriteAllText(PathOf("b.rejoin.done"), "Gone plus InPlace snapshot recovered without removing live item");
            }
        }
#endif
    }

    static void Finish()
    {
        done = true;
#if SERVER
        GameMain.ShouldRun = false;
#else
        GameMain.Instance?.Exit();
#endif
    }
}

