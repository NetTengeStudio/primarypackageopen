using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

internal static class Contracts {
    private static readonly BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static object Run(string game, string module) {
        // One contract check per short-lived process. MonoMod's generated IL proxy
        // cannot reference collectible assemblies, just as the real game uses a
        // non-collectible context for these dependencies.
        var context = new AssemblyLoadContext("Primary contracts", isCollectible: false);
        context.Resolving += (_, name) => {
            string file = Path.Combine(game, name.Name + ".dll");
            return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
        };
        try {
            Assembly mod = context.LoadFromAssemblyPath(module);
            bool server = Path.GetFileName(Path.GetDirectoryName(module)) == "server";
            Assembly client = context.LoadFromAssemblyPath(Path.Combine(game, server ? "DedicatedServer.dll" : "Barotrauma.dll"));
            Assembly core = context.LoadFromAssemblyPath(Path.Combine(game, "BarotraumaCore.dll"));
            Type Engine(string name) => client.GetType(name) ?? core.GetType(name) ?? throw new TypeLoadException(name);
            var methods = new List<string>();
            var clientTargets = new[] { ("Barotrauma.Networking.GameClient", "SendIngameUpdate", 0), ("Barotrauma.Networking.GameClient", "ReadIngameUpdate", 1), ("Barotrauma.Camera", "MoveCamera", 5), ("Barotrauma.Character", "get_IsMouseOnUI", 0), ("Barotrauma.Submarine", "DrawFront", 3), ("Barotrauma.Items.Components.LightComponent", "SetLightSourceState", 2), ("Barotrauma.Character", "UpdateLocalCursor", 1), ("Barotrauma.Character", "DoInteractionUpdate", 2), ("Barotrauma.AnimController", "UpdateAnimations", 1), ("Barotrauma.AnimController", "HandIK", 5) };
            var serverTargets = new[] { ("Barotrauma.Character", "TeleportTo", 1), ("Barotrauma.Networking.GameServer", "SetClientCharacter", 2), ("Barotrauma.Networking.GameServer", "ClientReadIngame", 1), ("Barotrauma.Networking.GameServer", "ClientWriteIngame", 1), ("Barotrauma.Networking.ServerEntityEventManager", "Update", 1) };
            foreach (var target in server ? serverTargets : clientTargets) {
                MethodInfo[] found = Engine(target.Item1).GetMethods(flags).Where(m => m.Name == target.Item2).ToArray();
                if (found.Length != 1 || found[0].GetParameters().Length != target.Item3) { throw new InvalidOperationException("Ambiguous or missing patch: " + target.Item1 + "." + target.Item2); }
                methods.Add(found[0].ToString());
            }
            int bootstrapFinalizeCalls = 0, bootstrapInjectedMarkers = 0;
            if (!server) {
                var start = Engine("Barotrauma.Networking.GameClient").GetMethod("StartGame", flags);
                var iterator = start.GetCustomAttribute<System.Runtime.CompilerServices.IteratorStateMachineAttribute>()?.StateMachineType;
                var moveNext = iterator?.GetMethod("MoveNext", flags) ?? throw new InvalidOperationException("Native startup iterator is missing");
                bootstrapFinalizeCalls = CountCalls(moveNext, "Barotrauma.Screen", "Select", "GameScreen");
                if (bootstrapFinalizeCalls != 1) { throw new InvalidOperationException("Native finalize marker is ambiguous: " + bootstrapFinalizeCalls); }
                var harmony = context.Assemblies.FirstOrDefault(a => a.GetName().Name == "0Harmony") ?? context.LoadFromAssemblyPath(Path.Combine(game, "0Harmony.dll"));
                var originalInstructions = harmony.GetType("HarmonyLib.PatchProcessor", true).GetMethods(flags).Single(m => m.Name == "GetOriginalInstructions" && m.GetParameters().Length == 2 && m.GetParameters()[1].IsOut);
                object instructions = originalInstructions.Invoke(null, new object[] { moveNext, null });
                var transform = mod.GetType("PrimaryPackage.Interactions.InteractionClient", true).GetMethod("FinalizeNativeWorld", flags);
                var transformed = (System.Collections.IEnumerable)transform.Invoke(null, new[] { instructions });
                foreach (object instruction in transformed) {
                    object operand = instruction.GetType().GetField("operand", flags).GetValue(instruction);
                    if (operand is MethodInfo called && called.Name == "NativeRoundReady" && called.DeclaringType?.FullName == "PrimaryPackage.Interactions.InteractionClient") { bootstrapInjectedMarkers++; }
                }
                if (bootstrapInjectedMarkers != 1) { throw new InvalidOperationException("Actual startup transpiler did not inject exactly one readiness marker"); }
            }
            int submarineConstructors = Engine("Barotrauma.Submarine").GetConstructors(flags).Count(c => c.GetParameters().Length == 4);
            if (submarineConstructors != 1) { throw new InvalidOperationException("Native submarine constructor changed"); }
            Type fieldsType = mod.GetType("PrimaryPackage.Interactions.DeviceEditor", true);
            var devices = new List<string>();
            int parsedProperties = 0, deniedValues = 0;
            Type componentType = Engine("Barotrauma.Items.Components.ItemComponent");
            Type propertyType = Engine("Barotrauma.SerializableProperty");
            MethodInfo propertiesMethod = propertyType.GetMethod("GetProperties", flags, new[] { typeof(object) });
            MethodInfo schemaMethod = fieldsType.GetMethods(flags).Single(m => m.Name == "Schema" && m.GetParameters().Length == 2);
            foreach (Type type in componentType.Assembly.GetTypes().Where(t => componentType.IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters)) {
                object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
                object properties = propertiesMethod.Invoke(null, new[] { instance });
                var fields = (Array)schemaMethod.Invoke(null, new[] { (object)type, properties });
                foreach (object field in fields) {
                    Type fieldType = field.GetType();
                    object property = fieldType.GetField("Property", flags).GetValue(field);
                    var native = (PropertyInfo)propertyType.GetField("PropertyInfo", flags).GetValue(property);
                    string kind = fieldType.GetField("Kind", flags).GetValue(field).ToString();
                    devices.Add(type.Name + "." + native.Name + ":" + kind);
                    MethodInfo parse = fieldType.GetMethod("Parse", flags);
                    string[] invalid = kind switch {
                        "Boolean" => new[] { "1", "True", "NaN" },
                        "Integer" => new[] { "1.5", "2147483648", "NaN" },
                        "Number" => new[] { "NaN", "Infinity", "-Infinity", "1e999" },
                        "Color" => new[] { "256,0,0,255", "0,0,0,-1", "0,0,0", "0.5,1,2,3", "NaN,0,0,0" },
                        "Vector2" => new[] { "NaN,0", "0,Infinity", "1", "1,2,3" },
                        "Enumeration" => new[] { "2147483647", "no_such_value" },
                        _ => new[] { "<markup>", "line\nbreak" }
                    };
                    foreach (string value in invalid) {
                        try { parse.Invoke(field, new object[] { value }); throw new InvalidOperationException("Invalid native value accepted: " + type.Name + "." + native.Name + "=" + value); }
                        catch (TargetInvocationException invalidValue) when (invalidValue.InnerException is ArgumentException) { deniedValues++; }
                    }
                    parsedProperties++;
                }
            }
            devices.Sort(StringComparer.Ordinal);
            if (!devices.Contains("Door.IsOpen:Boolean") || !devices.Any(d => d.StartsWith("LightComponent.") && d.EndsWith(":Color")) || devices.Count < 30) { throw new InvalidOperationException("Live schema is unexpectedly empty or lacks native door/light support"); }
            Type messageType = Engine("Barotrauma.Networking.ReadWriteMessage");
            Type programType = mod.GetType("PrimaryPackage.Interactions.SceneProgram", true);
            Type bindingsType = mod.GetType("PrimaryPackage.Interactions.SceneBindings", true);
            Type wire = mod.GetType("PrimaryPackage.Interactions.SceneWire", true);
            object program = JsonSerializer.Deserialize("{\"Name\":\"Дверь → персональная сцена\",\"Trigger\":1,\"Repeat\":true,\"Cooldown\":5,\"Steps\":[{\"Action\":2,\"Text\":\"Звук за дверью\",\"Delay\":1.5,\"Duration\":3.25},{\"Action\":3,\"Visual\":\"crawler\",\"Behavior\":5}]}", programType);
            object bindings = Activator.CreateInstance(bindingsType, new object[] { "Steam64:123456", (ushort)13, (ushort)17, (byte)2, (ushort)9, 123.5f, -777f, 500f });
            object output = Activator.CreateInstance(messageType);
            wire.GetMethod("Write", flags).Invoke(null, new[] { output, program, bindings });
            int length = (int)messageType.GetProperty("LengthBits").GetValue(output);
            byte[] buffer = (byte[])messageType.GetProperty("Buffer").GetValue(output);
            messageType.GetProperty("BitPosition").SetValue(output, 0);
            object[] read = { output, null };
            object decoded = wire.GetMethod("Read", flags).Invoke(null, read);
            object encoded = Activator.CreateInstance(messageType);
            wire.GetMethod("Write", flags).Invoke(null, new[] { encoded, decoded, read[1] });
            byte[] again = (byte[])messageType.GetProperty("Buffer").GetValue(encoded);
            int decodedLength = (int)messageType.GetProperty("LengthBits").GetValue(encoded);
            if (length != decodedLength || !buffer.AsSpan(0, (length + 7) / 8).SequenceEqual(again.AsSpan(0, (length + 7) / 8))) { throw new InvalidOperationException("Native scene wire roundtrip differs"); }
            int rejected = 0;
            for (int size = 0; size < (length + 7) / 8; size++) {
                byte[] truncated = buffer.AsSpan(0, size).ToArray();
                object input = Activator.CreateInstance(messageType, new object[] { truncated, 0, size * 8, false });
                try { wire.GetMethod("Read", flags).Invoke(null, new object[] { input, null }); }
                catch (TargetInvocationException) { rejected++; }
            }
            if (rejected != (length + 7) / 8) { throw new InvalidOperationException($"Truncated scene accepted: rejected {rejected} of {(length + 7) / 8}"); }
            Type transformType = mod.GetType("PrimaryPackage.Interactions.LiveItemTransform", true);
            MethodInfo decodeTransform = transformType.GetMethod("Decode", flags);
            Guid frameId = Guid.NewGuid();
            object framePosition = decodeTransform.Invoke(null, new object[] { frameId.ToString("N") + "|120,-30" });
            if ((Guid)framePosition.GetType().GetProperty("Frame").GetValue(framePosition) != frameId) { throw new InvalidOperationException("Transform frame identity did not roundtrip"); }
            foreach (string invalidPosition in new[] { "120,-30", Guid.Empty.ToString("N") + "|0,0", frameId.ToString("N") + "|NaN,0", frameId.ToString("N") + "|1.5,2", frameId.ToString("N") + "|10000001,0" }) {
                try { decodeTransform.Invoke(null, new object[] { invalidPosition }); throw new InvalidOperationException("Invalid transform frame accepted"); }
                catch (TargetInvocationException invalidFrame) when (invalidFrame.InnerException is ArgumentException) { }
            }
            if (Convert.ToInt32(Enum.Parse(Engine("Barotrauma.Networking.ChatMessageType"), "ServerMessageBoxInGame")) != 11) { throw new InvalidOperationException("Native in-game message type changed"); }
            Type announcementType = mod.GetType("PrimaryPackage.Interactions.GMAnnouncement", true);
            object announcement = Activator.CreateInstance(announcementType, true);
            void SetAnnouncement(string name, object value) => announcementType.GetProperty(name, flags).SetValue(announcement, value);
            SetAnnouncement("Id", Guid.NewGuid()); SetAnnouncement("Format", (byte)11); SetAnnouncement("Text", "Цветное сообщение игроку"); SetAnnouncement("Target", "Steam64:123456");
            SetAnnouncement("Sender", Enum.ToObject(mod.GetType("PrimaryPackage.Interactions.GMMessageSender", true), 5)); SetAnnouncement("CustomSender", "Капитан");
            object announcementMessage = Activator.CreateInstance(messageType);
            announcementType.GetMethod("Write", flags).Invoke(announcement, new[] { announcementMessage });
            int announcementBits = (int)messageType.GetProperty("LengthBits").GetValue(announcementMessage);
            byte[] announcementBuffer = (byte[])messageType.GetProperty("Buffer").GetValue(announcementMessage);
            messageType.GetProperty("BitPosition").SetValue(announcementMessage, 0);
            object receivedAnnouncement = announcementType.GetMethod("Read", flags).Invoke(null, new[] { announcementMessage });
            object rewrittenAnnouncement = Activator.CreateInstance(messageType);
            announcementType.GetMethod("Write", flags).Invoke(receivedAnnouncement, new[] { rewrittenAnnouncement });
            int rewrittenBits = (int)messageType.GetProperty("LengthBits").GetValue(rewrittenAnnouncement);
            byte[] rewrittenBuffer = (byte[])messageType.GetProperty("Buffer").GetValue(rewrittenAnnouncement);
            if (announcementBits != rewrittenBits || !announcementBuffer.AsSpan(0, (announcementBits + 7) / 8).SequenceEqual(rewrittenBuffer.AsSpan(0, (rewrittenBits + 7) / 8))) { throw new InvalidOperationException("GM announcement wire differs after roundtrip"); }
            int rejectedAnnouncements = 0;
            for (int size = 0; size < (announcementBits + 7) / 8; size++) {
                object input = Activator.CreateInstance(messageType, new object[] { announcementBuffer.AsSpan(0, size).ToArray(), 0, size * 8, false });
                try { announcementType.GetMethod("Read", flags).Invoke(null, new[] { input }); }
                catch (TargetInvocationException) { rejectedAnnouncements++; }
            }
            if (rejectedAnnouncements != (announcementBits + 7) / 8) { throw new InvalidOperationException("A truncated GM announcement was accepted"); }
            foreach (byte format in new byte[] { 0, 1, 4, 8, 253 }) {
                SetAnnouncement("Format", format);
                try { announcementType.GetMethod("Validate", flags).Invoke(announcement, null); throw new InvalidOperationException("Unsupported GM format accepted"); }
                catch (TargetInvocationException invalidFormat) when (invalidFormat.InnerException is ArgumentException) { }
            }
            foreach (byte format in new byte[] { 3, 7, 10, 11, 254, 255 }) {
                SetAnnouncement("Format", format); announcementType.GetMethod("Validate", flags).Invoke(announcement, null);
            }
            Type receiptType = mod.GetType("PrimaryPackage.Interactions.GMAnnouncementReceipt", true);
            object receipt = Activator.CreateInstance(receiptType, true);
            receiptType.GetProperty("Id", flags).SetValue(receipt, Guid.NewGuid()); receiptType.GetProperty("Sender", flags).SetValue(receipt, "Капитан");
            receiptType.GetProperty("Recipients", flags).SetValue(receipt, 128); receiptType.GetProperty("Timestamp", flags).SetValue(receipt, 1700000000000L);
            object receiptMessage = Activator.CreateInstance(messageType); receiptType.GetMethod("Write", flags).Invoke(receipt, new[] { receiptMessage });
            int receiptBits = (int)messageType.GetProperty("LengthBits").GetValue(receiptMessage);
            byte[] receiptBuffer = (byte[])messageType.GetProperty("Buffer").GetValue(receiptMessage);
            messageType.GetProperty("BitPosition").SetValue(receiptMessage, 0);
            object receivedReceipt = receiptType.GetMethod("Read", flags).Invoke(null, new[] { receiptMessage });
            if ((int)receiptType.GetProperty("Recipients", flags).GetValue(receivedReceipt) != 128 || (string)receiptType.GetProperty("Sender", flags).GetValue(receivedReceipt) != "Капитан") { throw new InvalidOperationException("Announcement receipt did not roundtrip"); }
            int rejectedReceipts = 0;
            for (int size = 0; size < (receiptBits + 7) / 8; size++) {
                object input = Activator.CreateInstance(messageType, new object[] { receiptBuffer.AsSpan(0, size).ToArray(), 0, size * 8, false });
                try { receiptType.GetMethod("Read", flags).Invoke(null, new[] { input }); }
                catch (TargetInvocationException) { rejectedReceipts++; }
            }
            if (rejectedReceipts != (receiptBits + 7) / 8) { throw new InvalidOperationException("Truncated announcement receipt accepted"); }
            object translationCodec = VerifyTranslationCodec(mod, messageType);
            return new { module = Path.GetFileName(Path.GetDirectoryName(module)), engineModules = new[] { client.ManifestModule.ModuleVersionId, core.ManifestModule.ModuleVersionId }, patchMethods = methods, bootstrapFinalizeCalls, bootstrapInjectedMarkers, submarineConstructors, devices, parsedProperties, deniedValues, sceneWireBits = length, truncatedPacketsRejected = rejected, announcementBits, rejectedAnnouncements, receiptBits, rejectedReceipts, transformFrameCases = 6, translationCodec, scope = "installed metadata, actual startup IL transformation and native message codec; no engine initialization, renderer or network connection" };
        } finally { if (context.IsCollectible) { context.Unload(); } }
    }
    private static int CountCalls(MethodInfo method, string declaringType, string name, string receiverField) {
        var single = new System.Reflection.Emit.OpCode[256]; var wide = new System.Reflection.Emit.OpCode[256];
        foreach (var field in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
            if (field.GetValue(null) is System.Reflection.Emit.OpCode op) { if (op.Size == 1) { single[(byte)op.Value] = op; } else { wide[(byte)op.Value] = op; } }
        }
        byte[] il = method.GetMethodBody().GetILAsByteArray(); int calls = 0; string previousField = null;
        for (int at = 0; at < il.Length;) {
            var op = il[at] == 0xfe ? wide[il[++at]] : single[il[at]]; at++;
            int size = op.OperandType switch {
                System.Reflection.Emit.OperandType.InlineNone => 0,
                System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                System.Reflection.Emit.OperandType.InlineVar => 2,
                System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                _ => 4
            };
            if (op.OperandType == System.Reflection.Emit.OperandType.InlineMethod) {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, at));
                if (called.DeclaringType?.FullName == declaringType && called.Name == name && previousField == receiverField) { calls++; }
            }
            if (op == System.Reflection.Emit.OpCodes.Ldsfld) { previousField = method.Module.ResolveField(BitConverter.ToInt32(il, at)).Name; }
            else if (op != System.Reflection.Emit.OpCodes.Nop) { previousField = null; }
            at += size;
        }
        return calls;
    }
    private static object VerifyTranslationCodec(Assembly mod, Type messageType) {
        Type codec = mod.GetType("PrimaryPackage.Interactions.TranslationWire", true);
        Type intentType = codec.GetNestedType("IntentRow", flags), stateType = codec.GetNestedType("StateRow", flags);
        int roundtrips = 0, truncated = 0, invalid = 0, maximumIntentBytes = 0, maximumStateBytes = 0;
        byte[] Buffer(object message) {
            int bits = (int)messageType.GetProperty("LengthBits").GetValue(message);
            return ((byte[])messageType.GetProperty("Buffer").GetValue(message)).AsSpan(0, (bits + 7) / 8).ToArray();
        }
        object Message(byte[] bytes) => Activator.CreateInstance(messageType, new object[] { bytes, 0, bytes.Length * 8, false });
        void RejectWire(Action action) {
            try { action(); throw new InvalidOperationException("Invalid translation wire accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException or FormatException or System.IO.EndOfStreamException or IndexOutOfRangeException) { invalid++; }
        }
        foreach (int count in new[] { 1, 64 }) {
            Guid request = Guid.NewGuid(); var intents = Array.CreateInstance(intentType, count); var states = Array.CreateInstance(stateType, count);
            string position = Guid.NewGuid().ToString("N") + "|120,-30", after = position[..33] + "220,-10";
            for (int n = 0; n < count; n++) {
                Guid token = Guid.NewGuid();
                string target = count > 1 ? (n == 0 ? position : position[..33] + (220 + n) + "," + (-10 - n)) : after;
                intents.SetValue(Activator.CreateInstance(intentType, new object[] { token, (ulong)123, position, target }), n);
                states.SetValue(Activator.CreateInstance(stateType, new object[] { token, (ushort)65532, target }), n);
            }
            foreach (bool state in new[] { false, true }) {
                MethodInfo write = codec.GetMethod(state ? "WriteState" : "WriteIntent", flags), read = codec.GetMethod(state ? "ReadState" : "ReadIntent", flags);
                object output = Activator.CreateInstance(messageType);
                write.Invoke(null, state ? new object[] { output, request, (ulong)125, states } : new object[] { output, request, intents });
                byte[] encoded = Buffer(output); object[] args = state ? new object[] { Message(encoded), null, null } : new object[] { Message(encoded), null };
                object rows = read.Invoke(null, args);
                if ((Guid)args[1] != request || state && (ulong)args[2] != 125) { throw new InvalidOperationException("Translation identity/revision changed in codec"); }
                object rewritten = Activator.CreateInstance(messageType);
                write.Invoke(null, state ? new object[] { rewritten, args[1], args[2], rows } : new object[] { rewritten, args[1], rows });
                if (!encoded.AsSpan().SequenceEqual(Buffer(rewritten))) { throw new InvalidOperationException("Translation wire roundtrip differs"); }
                roundtrips++;
                if (state) { maximumStateBytes = Math.Max(maximumStateBytes, encoded.Length); } else { maximumIntentBytes = Math.Max(maximumIntentBytes, encoded.Length); }
                for (int size = 0; size < encoded.Length; size++) {
                    object[] truncatedArgs = state ? new object[] { Message(encoded.AsSpan(0, size).ToArray()), null, null } : new object[] { Message(encoded.AsSpan(0, size).ToArray()), null };
                    try { read.Invoke(null, truncatedArgs); throw new InvalidOperationException("Truncated translation accepted at " + size); }
                    catch (TargetInvocationException) { truncated++; }
                }
            }
            var badRows = Array.CreateInstance(intentType, 2);
            badRows.SetValue(intents.GetValue(0), 0); badRows.SetValue(intents.GetValue(0), 1);
            RejectWire(() => codec.GetMethod("WriteIntent", flags).Invoke(null, new object[] { Activator.CreateInstance(messageType), request, badRows }));
            badRows.SetValue(Activator.CreateInstance(intentType, new object[] { Guid.NewGuid(), (ulong)123, position, Guid.NewGuid().ToString("N") + "|221,-10" }), 1);
            RejectWire(() => codec.GetMethod("WriteIntent", flags).Invoke(null, new object[] { Activator.CreateInstance(messageType), request, badRows }));
            var badStates = Array.CreateInstance(stateType, 2);
            badStates.SetValue(states.GetValue(0), 0); badStates.SetValue(states.GetValue(0), 1);
            RejectWire(() => codec.GetMethod("WriteState", flags).Invoke(null, new object[] { Activator.CreateInstance(messageType), request, (ulong)125, badStates }));
        }
        var empty = Array.CreateInstance(intentType, 0); var oversized = Array.CreateInstance(intentType, 65);
        foreach (var rows in new[] { empty, oversized }) { RejectWire(() => codec.GetMethod("WriteIntent", flags).Invoke(null, new object[] { Activator.CreateInstance(messageType), Guid.NewGuid(), rows })); }
        return new { roundtrips, truncatedPacketsRejected = truncated, invalidGroupsRejected = invalid, maximumIntentBytes, maximumStateBytes, scope = "native codec only; no network delivery" };
    }

}
