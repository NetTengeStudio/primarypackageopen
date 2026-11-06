using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Barotrauma;
using HarmonyLib;
using IEngine.StatusEffects;
using Label = System.Reflection.Emit.Label;

namespace IEngine.Execution;

sealed class ContainedTargetsFeature : Feature
{
    static ContainedTargetsFeature instance;
    static MethodInfo apply, contents;
    static MethodBase[] observed;
    static int sites;
    static string fallback, observers;
    static volatile bool on;

    public override string Name => "contents";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        apply = AccessTools.Method(typeof(Item), nameof(Item.ApplyStatusEffect));
        if (!Matches(apply, "2ac66015576154e8e348db59c9af50a27bdf0cc11901f201b056c4c14d6cca05", "bc62c853f7db6b64fb3eaf5c526d23c30153855528c7316333c4446907a59a0c") ||
            apply.GetMethodBody().LocalVariables.Count == 0 || apply.GetMethodBody().LocalVariables[0].LocalType != typeof(bool))
        {
            return "Item.ApplyStatusEffect differs from the verified client/server builds";
        }
        contents = AccessTools.PropertyGetter(typeof(Item), nameof(Item.ContainedItems));
        var methods = new List<MethodBase> { AccessTools.PropertyGetter(typeof(Item), nameof(Item.OwnInventory)) };
        Type containedState = ObserveIterator(methods, contents);
        MethodInfo all = AccessTools.PropertyGetter(typeof(Inventory), nameof(Inventory.AllItems));
        methods.Add(all);
        MethodInfo enumerate = AccessTools.Method(typeof(Inventory), nameof(Inventory.GetAllItems));
        Type inventoryState = ObserveIterator(methods, enumerate);
        if (!Matches(contents, "4eb903f01a08c2702ecccb578780af4c9607f3c4ffc3872d513d479a6a1fe536", "6ac680d5261218215a12c4d5355e401aed19ce37d6b858e72be4e538bc4e0d98") ||
            !Matches(AccessTools.Method(containedState, "MoveNext"), "abcd9d59a6f2a6535462b6f7709b5f59a2665498939e6f3d17073c474c797ae1", "6d78d861d9832979efb0fbfb752050b0d9cd491fa732cb23a48ad49b6a6b60d5") ||
            !Matches(all, "a87ab374a5185c25f35a188de72b3721aea47b24f7973ad10448a3a88a6b7e40", "2c0cc944539e80b275fd90d89da0222ee8a2facdcfb59683c75911eb61bb2e21") ||
            !Matches(enumerate, "f702a7ada2c9d2c499f032ddce73dc591cee226d9eb7b789f74c8df3a5cb4446", "725db0d84a463a42bbf44bae9e6c773595ddef1f132c9b5e6a52f940b2896f55") ||
            !Matches(AccessTools.Method(inventoryState, "MoveNext"), "79fb3dc4e32594996c88687eea33480fc03a9a7518183f171be554be1410a567", "5b4645b9400c6b409e8bf99f68620110c1de53977785d8b9ca8b15e9d75f26a2"))
        {
            return "Inventory enumeration differs from the verified client/server builds";
        }
        foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(apply))
        {
            if (instruction.operand is MethodInfo called && called.DeclaringType == typeof(System.Linq.Enumerable) && called.Name == "Any") { methods.Add(called); }
            if (instruction.opcode == OpCodes.Ldftn && instruction.operand is MethodInfo predicate && predicate.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType == typeof(Identifier))
            {
                methods.Add(predicate);
                methods.AddRange(predicate.DeclaringType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            }
        }
        methods.Add(AccessTools.Method(typeof(ImmutableHashSet<Identifier>), "GetEnumerator", Type.EmptyTypes));
        methods.AddRange(typeof(ImmutableHashSet<Identifier>).GetInterfaceMap(typeof(IEnumerable<Identifier>)).TargetMethods);
        observed = methods.ToArray();
        harmony.Patch(apply, transpiler: new HarmonyMethod(typeof(ContainedTargetsFeature), nameof(Transpile)) { priority = Priority.Last });
        return sites == 1 ? null : "Contained target loop was not specialized: " + fallback;
    }

    static bool Matches(MethodInfo method, string client, string server)
    {
        byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null) { return false; }
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        return hash.Equals(client, StringComparison.OrdinalIgnoreCase) || hash.Equals(server, StringComparison.OrdinalIgnoreCase);
    }

    static Type ObserveIterator(List<MethodBase> methods, MethodInfo getter)
    {
        methods.Add(getter);
        Type state = getter.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
        if (state == null) { throw new InvalidOperationException("Inventory iterator shape changed"); }
        methods.AddRange(state.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        methods.AddRange(state.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        return state;
    }

    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void Uninstall() => on = false;

    static void Refresh()
    {
        observers = null;
        PatchCensus.Foreign body = PatchCensus.Of(apply);
        if (body.Transpilers.Length != 0) { observers = $"{apply}: {body}"; }
        if (observers == null)
        {
            foreach (MethodBase method in observed)
            {
                PatchCensus.Foreign foreign = PatchCensus.Of(method);
                if (foreign.Any) { observers = $"{method.DeclaringType}.{method.Name}: {foreign}"; break; }
            }
        }
        on = instance.Active && sites == 1 && observers == null;
    }

    public static bool Append(Item owner, StatusEffect effect, List<ISerializableEntity> targets) => ContainedTargets.Append(owner, effect, targets, on);

    static bool TryAppend(Item owner, StatusEffect effect, List<ISerializableEntity> targets, ref bool matched)
    {
        if (!on) { return false; }
        matched |= ContainedTargets.Append(owner, effect, targets, true);
        return true;
    }

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = new List<CodeInstruction>(instructions);
        try
        {
            int start = -1;
            for (int i = 1; i < code.Count; i++)
            {
                if (!code[i].Calls(contents)) { continue; }
                if (start >= 0) { throw new InvalidOperationException("Multiple contained target loops"); }
                start = i - 1;
            }
            Validate(code, start);
            Label done = (Label)code[start + 53].operand;
            FieldInfo targets = AccessTools.Field(typeof(Item), "targets");
            var injected = new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0) { labels = code[start].labels },
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, targets),
                new CodeInstruction(OpCodes.Ldloca, 0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ContainedTargetsFeature), nameof(TryAppend))),
                new CodeInstruction(OpCodes.Brtrue, done)
            };
            code[start].labels = new List<Label>();
            code[start + 1] = new CodeInstruction(code[start + 1]) { operand = AccessTools.Method(typeof(ContainedTargets), nameof(ContainedTargets.ReadContents)) };
            code.InsertRange(start, injected);
            sites = 1;
            fallback = null;
        }
        catch (InvalidOperationException e) { sites = 0; fallback = e.Message; }
        return code;
    }

    static void Validate(List<CodeInstruction> code, int start)
    {
        string[] shape = ("ldarg.0 call callvirt stloc br newobj stloc ldloc ldloc callvirt stfld ldarg.1 ldfld brfalse " +
            "ldarg.1 ldfld ldloc ldfld ldfld ldfld callvirt brtrue ldarg.1 ldfld ldloc ldftn newobj call brfalse " +
            "ldarg.1 ldfld ldc.i4.m1 ble ldarg.0 call ldarg.1 ldfld callvirt ldloc ldfld call brfalse ldc.i4.1 stloc " +
            "ldarg.0 ldfld ldloc ldfld callvirt callvirt ldloc callvirt brtrue leave ldloc brfalse ldloc callvirt endfinally").Split(' ');
        if (shape.Length != 59 || start < 0 || start + 59 >= code.Count) { throw new InvalidOperationException("Incomplete contained target loop"); }
        for (int i = 0; i < shape.Length; i++)
        {
            string name = code[start + i].opcode.Name;
            if (name.StartsWith("ldloc", StringComparison.Ordinal)) { name = "ldloc"; }
            else if (name.StartsWith("stloc", StringComparison.Ordinal)) { name = "stloc"; }
            else if (name.EndsWith(".s", StringComparison.Ordinal)) { name = name.Substring(0, name.Length - 2); }
            if (name != shape[i]) { throw new InvalidOperationException("Contained target instruction contract changed"); }
        }
        if (code[start + 43].opcode != OpCodes.Stloc_0 || code[start].blocks.Count != 0 ||
            !Equals(code[start + 1].operand, contents) || code[start + 53].operand is not Label done || !code[start + 59].labels.Contains(done))
        {
            throw new InvalidOperationException("Contained target result or control flow changed");
        }
        FieldInfo identifiers = AccessTools.Field(typeof(StatusEffect), nameof(StatusEffect.TargetIdentifiers));
        FieldInfo slot = AccessTools.Field(typeof(StatusEffect), nameof(StatusEffect.TargetSlot));
        if (!code[start + 12].LoadsField(identifiers) || !code[start + 15].LoadsField(identifiers) || !code[start + 23].LoadsField(identifiers) ||
            !code[start + 30].LoadsField(slot) || !code[start + 36].LoadsField(slot) ||
            !code[start + 18].LoadsField(AccessTools.Field(typeof(MapEntity), nameof(MapEntity.Prefab))) ||
            !code[start + 19].LoadsField(AccessTools.Field(typeof(Prefab), nameof(Prefab.Identifier))) ||
            !code[start + 45].LoadsField(AccessTools.Field(typeof(Item), "targets")) ||
            !code[start + 34].Calls(AccessTools.PropertyGetter(typeof(Item), nameof(Item.OwnInventory))) ||
            !code[start + 37].Calls(AccessTools.Method(typeof(Inventory), nameof(Inventory.GetItemsAt))) ||
            !code[start + 48].Calls(AccessTools.PropertyGetter(typeof(Item), nameof(Item.AllPropertyObjects))))
        {
            throw new InvalidOperationException("Contained target data contract changed");
        }
        for (int i = 0; i < shape.Length; i++)
        {
            foreach (ExceptionBlock block in code[start + i].blocks)
            {
                if (i != 4 && i != 54 && i != 58 ||
                    block.blockType != (i == 4 ? ExceptionBlockType.BeginExceptionBlock : i == 54 ? ExceptionBlockType.BeginFinallyBlock : ExceptionBlockType.EndExceptionBlock))
                {
                    throw new InvalidOperationException("Contained target exception regions changed");
                }
            }
        }
    }

    public override string Report() => $"active={on} containedLoops={sites}" +
        (observers == null ? "" : $" disabled: observed by {observers}") +
        (fallback == null ? "" : " fallback: " + fallback);
}
