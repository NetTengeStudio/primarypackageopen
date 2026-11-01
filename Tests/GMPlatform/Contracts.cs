using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

internal static class Contracts {
    private static readonly BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static object Run(string game, string module) {
        var context = new AssemblyLoadContext("Primary contracts", isCollectible: true);
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
            var clientTargets = new[] { ("Barotrauma.Submarine", "DrawFront", 3), ("Barotrauma.Items.Components.LightComponent", "SetLightSourceState", 2), ("Barotrauma.Character", "UpdateLocalCursor", 1), ("Barotrauma.Character", "DoInteractionUpdate", 2), ("Barotrauma.AnimController", "UpdateAnimations", 1), ("Barotrauma.AnimController", "HandIK", 5) };
            var serverTargets = new[] { ("Barotrauma.Character", "TeleportTo", 1), ("Barotrauma.Networking.GameServer", "SetClientCharacter", 2) };
            foreach (var target in server ? serverTargets : clientTargets) {
                MethodInfo[] found = Engine(target.Item1).GetMethods(flags).Where(m => m.Name == target.Item2).ToArray();
                if (found.Length != 1 || found[0].GetParameters().Length != target.Item3) { throw new InvalidOperationException("Ambiguous or missing patch: " + target.Item1 + "." + target.Item2); }
                methods.Add(found[0].ToString());
            }
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
            void SetAnnouncement(string name, object value) => announcementType.GetField(name, flags).SetValue(announcement, value);
            SetAnnouncement("Id", Guid.NewGuid()); SetAnnouncement("Format", (byte)11); SetAnnouncement("Text", "Цветное сообщение игроку"); SetAnnouncement("Target", "Steam64:123456");
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
            foreach (byte format in new byte[] { 0, 1, 4, 8, 254 }) {
                SetAnnouncement("Format", format);
                try { announcementType.GetMethod("Validate", flags).Invoke(announcement, null); throw new InvalidOperationException("Unsupported GM format accepted"); }
                catch (TargetInvocationException invalidFormat) when (invalidFormat.InnerException is ArgumentException) { }
            }
            return new { module = Path.GetFileName(Path.GetDirectoryName(module)), engineModules = new[] { client.ManifestModule.ModuleVersionId, core.ManifestModule.ModuleVersionId }, patchMethods = methods, devices, parsedProperties, deniedValues, sceneWireBits = length, truncatedPacketsRejected = rejected, announcementBits, rejectedAnnouncements, transformFrameCases = 6, scope = "installed managed metadata and native message codec; no engine initialization, renderer or network connection" };
        } finally { context.Unload(); }
    }
}
