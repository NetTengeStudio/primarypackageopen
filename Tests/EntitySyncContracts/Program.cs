#nullable enable
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using PrimaryPackage.SubEditor;

internal static class Program
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

    private delegate bool Append(ulong revision, ReadOnlySpan<byte> bytes);
    private static int checks;
    private static void Check(bool pass, string name)
    {
        if (!pass) { throw new InvalidOperationException(name); }
        checks++; Console.WriteLine("PASS " + name);
    }
    private static object Blank(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo Method(Type type, string name) => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
    private static void Set(object target, string name, object? value)
    {
        if (AccessTools.Property(target.GetType(), name) is { } property) { property.SetValue(target, value); }
        else { Field(target.GetType(), name).SetValue(target, value); }
    }
    private static byte[] Frame(ulong revision, ushort cut, OpKind kind = OpKind.Spawn)
    {
        byte[] bytes = new byte[64];
        int length = OpFrame.Write(bytes, kind, 1, revision, revision - 1, new ushort[] { 32763 }, Array.Empty<byte>(), cut);
        return bytes.AsSpan(0, length).ToArray();
    }
    private static bool MoreRecent(ushort a, ushort b) => unchecked((short)(a - b)) > 0;

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("EntitySyncContracts <game> <client-mod> <server-mod> <server>"); return 2; }
        try
        {
            Ordering(); Server(args[0], args[2], args[3]); Client(args[0], args[1]);
            Console.WriteLine($"EntitySyncContracts: {checks} checks, 0 failures; no game process or game loop");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Ordering()
    {
        ushort native = 99;
        var applied = new List<ulong>();
        var replica = new Replica(v => { applied.Add(v.Rev); return true; }, v => !MoreRecent(v.NativeEventId, native));
        Check(replica.BeginAt(0), "replica begins at snapshot cut");
        Check(replica.Offer(Frame(2, 102, OpKind.Delete)) == OfferResult.Buffered, "reordered delete waits for spawn");
        Check(replica.Offer(Frame(1, 100)) == OfferResult.Buffered && replica.Applied == 0 && replica.Active, "spawn waits for pre-edit native events without failing the replica");
        Check(replica.Drain() && applied.Count == 0, "repeated drain cannot bypass native prerequisite");
        Check(!replica.TryGetGap(0, 500, out _, out _), "native prerequisite is not misreported as a missing editor revision");
        native = 100;
        Check(replica.Offer(Frame(1, 100)) == OfferResult.Duplicate && replica.Applied == 0, "duplicate arriving as native fence opens cannot leave a stale pending head");
        Check(replica.Drain() && replica.Applied == 1 && applied.SequenceEqual(new ulong[] { 1 }), "spawn applies exactly at its native cut while later deletion waits");
        Check(replica.Offer(Frame(1, 100)) == OfferResult.Duplicate, "duplicate operation does not recreate an entity");
        native = 102;
        Check(replica.Drain() && replica.Applied == 2 && applied.SequenceEqual(new ulong[] { 1, 2 }), "delete applies only after preceding native state is consumed");
        replica.Reset(); native = 65534;
        Check(replica.Offer(Frame(1, 65535)) == OfferResult.Buffered && replica.BeginAt(0) && replica.Applied == 0, "snapshot drain preserves native fence at ushort wrap");
        native = 0;
        Check(replica.Drain() && replica.Applied == 1, "native fence releases after sequence wraps to zero");
        Check(replica.Offer(Frame(2, 0).AsSpan(0, 3)) == OfferResult.Malformed && replica.Applied == 1, "truncated revision never advances application acknowledgement");
        var failed = new Replica(_ => false, _ => true);
        failed.BeginAt(0);
        Check(failed.Offer(Frame(1, 0)) == OfferResult.Failed && failed.Applied == 0, "failed mutation cannot be acknowledged as applied");
        var baseline = new Replica(_ => true);
        baseline.BeginAt(0);
        Check(baseline.Offer(Frame(1, 100)) == OfferResult.Applied, "control: original unfenced replica applies before native prerequisite");
    }

    private static void Server(string game, string modPath, string serverRoot)
    {
        var context = new Context(game, serverRoot);
        Assembly engine = context.LoadFromAssemblyPath(Path.Combine(serverRoot, "DedicatedServer.dll"));
        Assembly mod = context.LoadFromAssemblyPath(Path.GetFullPath(modPath));
        Type Native(string name) => engine.GetType("Barotrauma.Networking." + name, true)!;
        Type Patch(string name) => mod.GetType("PrimaryPackage.SubEditor." + name, true)!;
        Type serverType = Patch("SubEditorServer"), sessionType = serverType.GetNestedType("Session", BindingFlags.NonPublic)!;
        object server = Activator.CreateInstance(serverType)!;
        object ring = Activator.CreateInstance(Patch("OpRing"), 4096, 16)!;
        Append append = Method(ring.GetType(), "Append").CreateDelegate<Append>(ring);
        Check(append(1, Frame(1, 100)) && append(2, Frame(2, 102, OpKind.Delete)), "server replay contains causal cuts for spawn and delete");
        object authority = Activator.CreateInstance(Patch("Authority"), ring, Activator.CreateInstance(Patch("EntityVersions")))!;
        Field(authority.GetType(), "<Rev>k__BackingField").SetValue(authority, 2UL);
        Field(serverType, "authority").SetValue(server, authority);
        Field(serverType, "instance").SetValue(null, server);
        object client = Blank(Native("Client")); Set(client, "SessionId", (byte)1); Set(client, "LastRecvEntityEventID", (ushort)98);
        object session = Activator.CreateInstance(sessionType, nonPublic: true)!;
        Set(session, "Client", client); Set(session, "Prepared", true); Set(session, "Live", true);
        IDictionary sessions = (IDictionary)Field(serverType, "sessions").GetValue(server)!; sessions.Add((byte)1, session);
        Type nativeEvent = Native("ServerEntityEvent"), managerType = Native("ServerEntityEventManager");
        object manager = Blank(managerType), host = Blank(Native("GameServer"));
        object settings = Blank(Native("ServerSettings")); Set(settings, "TickRate", 60); Set(host, "ServerSettings", settings);
        Field(managerType, "server").SetValue(manager, host);
        IList Events(params ushort[] ids)
        {
            IList events = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(nativeEvent))!;
            foreach (ushort id in ids) { object e = Blank(nativeEvent); Field(nativeEvent, "ID").SetValue(e, id); events.Add(e); }
            return events;
        }
        Field(managerType, "events").SetValue(manager, Events(99, 100, 101, 102, 103, 104));
        Field(managerType, "uniqueEvents").SetValue(manager, Events(1, 2));
        Set(client, "UnreceivedEntityEventCount", (ushort)2);
        Field(Native("Client"), "EntityEventLastSent").SetValue(client, Activator.CreateInstance(Field(Native("Client"), "EntityEventLastSent").FieldType));
        IDictionary sent = (IDictionary)Field(Native("Client"), "EntityEventLastSent").GetValue(client)!;
        foreach (ushort id in new ushort[] { 0, 1, 2, 99, 100, 101, 102, 103, 104, 65535 }) { sent[id] = -10d; }
        MethodInfo get = Method(managerType, "GetEventsToSync");
        var harmony = new Harmony("primary.entity.sync.server.contracts");
        try
        {
            Method(Patch("NativeEventOrder"), "Attach").Invoke(null, new object[] { harmony });
            IList Read() => (IList)get.Invoke(manager, new[] { client })!;
            Check(Read().Count == 2, "installed native writer emits events through spawn cut and holds all later events");
            object remote = Activator.CreateInstance(sessionType, nonPublic: true)!;
            Set(remote, "Prepared", true); Set(remote, "Live", true); Set(remote, "AppliedRevision", 2UL);
            sessions[(byte)2] = remote;
            Set(client, "SessionId", (byte)2);
            Check(Read().Count == 6, "up-to-date peer continues while another peer waits for editor application");
            Set(client, "SessionId", (byte)1);
            Set(session, "AppliedRevision", 1UL);
            Check(Read().Count == 4, "applied spawn acknowledgement opens only the interval before deletion");
            Set(session, "AppliedRevision", 2UL);
            Check(Read().Count == 6, "applied deletion acknowledgement releases remaining native events");
            Set(session, "Prepared", false);
            Check(Read().Count == 0, "native stream waits while snapshot entities have not been built");
            Set(session, "Prepared", true); Set(session, "Live", false); Set(session, "SnapshotNativeId", (ushort)100);
            Check(Read().Count == 2, "snapshot handoff permits old native events but withholds post-snapshot state");
            Set(client, "NeedsMidRoundSync", true); Set(client, "LastRecvEntityEventID", (ushort)0);
            Check(Read().Count == 2, "prepared snapshot permits native initial sequence without comparing it to live IDs");
            Set(session, "Prepared", false);
            Check(Read().Count == 0, "native initial sequence also waits for snapshot entities");
            Set(client, "NeedsMidRoundSync", false); Set(client, "LastRecvEntityEventID", (ushort)98);
            sessions.Clear();
            Check(Read().Count == 0, "peer without an editor session cannot receive state for unknown editor entities");
            sessions.Add((byte)1, session); Set(session, "Prepared", true); Set(session, "Live", true); Set(session, "AppliedRevision", 0UL);
            Field(managerType, "events").SetValue(manager, Events(65535, 0, 1)); Set(client, "LastRecvEntityEventID", (ushort)65534);
            var wrapped = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Native("NetEntityEvent")))!;
            foreach (object e in (IList)Field(managerType, "events").GetValue(manager)!) { wrapped.Add(e); }
            // Same production filter, with a frame whose pre-edit cut is 65535.
            object wrapRing = Activator.CreateInstance(Patch("OpRing"), 4096, 16)!;
            Method(wrapRing.GetType(), "Append").CreateDelegate<Append>(wrapRing)(1, Frame(1, 65535));
            object wrapAuthority = Activator.CreateInstance(Patch("Authority"), wrapRing, Activator.CreateInstance(Patch("EntityVersions")))!;
            Field(wrapAuthority.GetType(), "<Rev>k__BackingField").SetValue(wrapAuthority, 1UL);
            Field(serverType, "authority").SetValue(server, wrapAuthority);
            Method(serverType, "FilterNativeEvents").Invoke(null, new object[] { client, wrapped });
            Check(wrapped.Count == 1, "server cutoff handles 65535-to-zero sequence wrap");
            object window = Activator.CreateInstance(Patch("ChunkWindow"), 6, 1000L, 15000L, 60000L, 0L)!;
            Method(window.GetType(), "OnProduced").Invoke(window, new object[] { 1 });
            Method(window.GetType(), "Take").Invoke(window, new object[] { 0L });
            Set(session, "Window", window); Set(session, "Produced", 1); Set(session, "Finished", true);
            Set(session, "Prepared", false); Set(session, "Live", false); Set(session, "SnapshotRevision", 1UL);
            Method(serverType, "OnAck").Invoke(server, new object[] { session, 1 });
            Check(Field(sessionType, "Prepared").GetValue(session) is true && Field(sessionType, "Live").GetValue(session) is true,
                "final snapshot ack releases handoff even when readiness packet arrives later");
        }
        finally { harmony.UnpatchSelf(); Field(serverType, "instance").SetValue(null, null); }
    }

    private static bool NotOwner(ref bool __result) { __result = false; return false; }

    private static void Client(string game, string modPath)
    {
        var context = new Context(game, game);
        Assembly engine = context.LoadFromAssemblyPath(Path.Combine(game, "Barotrauma.dll"));
        Assembly mod = context.LoadFromAssemblyPath(Path.GetFullPath(modPath));
        Type Native(string name) => engine.GetType("Barotrauma." + name, true)!;
        Type clientType = Native("Networking.GameClient"), entity = Native("Entity"), spawnerType = Native("EntitySpawner");
        object client = Blank(clientType), spawner = Blank(spawnerType);
        IList history = (IList)Activator.CreateInstance(Field(spawnerType, "receivedEvents").FieldType)!;
        Type tuple = history.GetType().GenericTypeArguments[0];
        history.Add(Activator.CreateInstance(tuple, new object?[] { null, true }));
        Field(spawnerType, "receivedEvents").SetValue(spawner, history); Field(entity, "Spawner").SetValue(null, spawner);
        var harmony = new Harmony("primary.entity.sync.client.contracts");
        string previous = Environment.CurrentDirectory;
        string scratch = Path.Combine(Path.GetTempPath(), "pp-entity-sync", "diagnostic-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Type networkContext = mod.GetType("PrimaryPackage.SubEditor.ClientNetworkContext", true)!;
            Method(networkContext, "Attach").Invoke(null, new object[] { harmony });
            Check(Harmony.GetPatchInfo(Method(clientType, "Update")).Owners.Contains(harmony.Id), "round-network patch installs on actual GameClient.Update");
            Type screenType = Native("Screen"), main = Native("GameMain");
            object roundScreen = Blank(Native("GameScreen"));
            Field(main, "GameScreen").SetValue(null, roundScreen);
            object editor = Blank(mod.GetType("PrimaryPackage.SubEditor.LiveEditorScreen", true)!);
            Field(editor.GetType(), "open").SetValue(editor, true);
            AccessTools.Property(screenType, "Selected").SetValue(null, editor);
            Check(ReferenceEquals(Method(networkContext, "RoundScreen").Invoke(null, null), roundScreen), "open live editor selects native ingame update path containing entity acknowledgements");
            Check(ReferenceEquals(AccessTools.Property(screenType, "Selected").GetValue(null), editor), "network classification preserves actual selected editor screen");
            Field(editor.GetType(), "open").SetValue(editor, false);
            Check(ReferenceEquals(Method(networkContext, "RoundScreen").Invoke(null, null), editor), "closed editor retains ordinary native screen classification");
            object lobby = Blank(Native("NetLobbyScreen")); AccessTools.Property(screenType, "Selected").SetValue(null, lobby);
            Check(ReferenceEquals(Method(networkContext, "RoundScreen").Invoke(null, null), lobby), "real lobby retains native lobby update path");
            AccessTools.Property(screenType, "Selected").SetValue(null, null);
            Field(main, "GameScreen").SetValue(null, null);
            harmony.Patch(AccessTools.PropertyGetter(clientType, "IsServerOwner"), prefix: new HarmonyMethod(Method(typeof(Program), nameof(NotOwner))));
            object patches = Activator.CreateInstance(mod.GetType("PrimaryPackage.Core.PatchSet", true)!)!;
            Method(mod.GetType("INet.EventErrorDiagnostics", true)!, "Attach").Invoke(null, new[] { harmony, patches });
            MethodInfo write = Method(clientType, "WriteEventErrorData");
            Check(Harmony.GetPatchInfo(write).Owners.Contains(harmony.Id), "diagnostic patch installs on actual client method");
            Environment.CurrentDirectory = scratch;
            write.Invoke(client, new object[] { Enum.Parse(Native("Networking.ClientNetError"), "MISSING_ENTITY"), (ushort)0, (ushort)4462, (ushort)32763 });
            Check(Field(clientType, "eventErrorWritten").GetValue(client) is true, "native error report completes with an absent-entity removal in spawner history");
            string report = File.ReadAllText(Directory.GetFiles(Path.Combine(scratch, "ServerLogs"), "event_error_log_client_*.log").Single());
            Check(report.Contains("32763") && report.Contains("<entity already absent>"), "diagnostic preserves original missing ID and absent removal evidence");
        }
        finally { Environment.CurrentDirectory = previous; harmony.UnpatchSelf(); Field(entity, "Spawner").SetValue(null, null); }
    }
}
