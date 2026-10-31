using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Barotrauma;
using HarmonyLib;
using Label = System.Reflection.Emit.Label;

namespace IEngine.Execution;

sealed class SpriteTraversalFeature : Feature
{
    static SpriteTraversalFeature instance;
    static MethodInfo update, contents;
    static MethodBase[] observed;
    static volatile bool on, emptyOn;
    static bool emptyVerified;
    static int sites;
    static string fallback, observers;

    public override string Name => "sprites";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        update = AccessTools.Method(typeof(Item), nameof(Item.UpdateSpriteStates));
        contents = AccessTools.PropertyGetter(typeof(Item), nameof(Item.ContainedItems));
        emptyVerified = Matches(update, "7b32078ddb63ca3598e79e75ab530a43f6a913ca1ff58e44b7c0205cf2899cdf");
        MethodInfo all = AccessTools.PropertyGetter(typeof(Inventory), nameof(Inventory.AllItems));
        MethodInfo enumerate = AccessTools.Method(typeof(Inventory), nameof(Inventory.GetAllItems));
        Type contentState = contents?.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
        Type inventoryState = enumerate?.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
        if (update == null || all == null || contentState == null || inventoryState == null)
        {
            return "Inventory enumeration contract is unavailable";
        }
        var methods = new List<MethodBase>
        {
            contents, all, enumerate, AccessTools.PropertyGetter(typeof(Item), nameof(Item.OwnInventory)),
            AccessTools.PropertyGetter(typeof(Item), nameof(Item.Prefab)),
            AccessTools.PropertyGetter(typeof(ItemPrefab), nameof(ItemPrefab.ContainedSprites)),
            AccessTools.PropertyGetter(typeof(ItemPrefab), nameof(ItemPrefab.DecorativeSpriteGroups))
        };
        methods.AddRange(contentState.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        methods.AddRange(inventoryState.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        methods.AddRange(contentState.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        methods.AddRange(inventoryState.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        observed = methods.ToArray();
        if (!Matches(contents, "4eb903f01a08c2702ecccb578780af4c9607f3c4ffc3872d513d479a6a1fe536") ||
            !Matches(AccessTools.Method(contentState, "MoveNext"), "abcd9d59a6f2a6535462b6f7709b5f59a2665498939e6f3d17073c474c797ae1") ||
            !Matches(all, "a87ab374a5185c25f35a188de72b3721aea47b24f7973ad10448a3a88a6b7e40") ||
            !Matches(enumerate, "f702a7ada2c9d2c499f032ddce73dc591cee226d9eb7b789f74c8df3a5cb4446") ||
            !Matches(AccessTools.Method(inventoryState, "MoveNext"), "79fb3dc4e32594996c88687eea33480fc03a9a7518183f171be554be1410a567"))
        {
            return "Inventory enumeration differs from the verified client build";
        }
        harmony.Patch(update, transpiler: new HarmonyMethod(typeof(SpriteTraversalFeature), nameof(Transpile)) { priority = Priority.Last });
        return sites == 1 ? null : "Contained sprite loop was not specialized: " + fallback;
    }

    static bool Matches(MethodInfo method, string expected)
    {
        byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
        return bytes != null && Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();

    static void Refresh()
    {
        observers = null;
        foreach (MethodBase method in observed)
        {
            PatchCensus.Foreign foreign = PatchCensus.Of(method);
            if (foreign.Any) { observers = $"{method.DeclaringType}.{method.Name}: {foreign}"; break; }
        }
        on = instance.Active && sites == 1 && observers == null;
        emptyOn = on && emptyVerified && PatchCensus.Of(update).Transpilers.Length == 0;
    }

    static void UpdateContents(Item item, float deltaTime)
    {
        if (!on)
        {
            foreach (Item child in ReadContents(item)) { child.UpdateSpriteStates(deltaTime); }
            return;
        }
        if (item.OwnInventories.Length < 2)
        {
            if (item.OwnInventory == null) { return; }
            UpdateInventory(item.OwnInventory, deltaTime);
        }
        else
        {
            foreach (ItemInventory inventory in item.OwnInventories) { UpdateInventory(inventory, deltaTime); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    static IEnumerable<Item> ReadContents(Item item) => item.ContainedItems;

    static void UpdateInventory(ItemInventory inventory, float deltaTime)
    {
        if (inventory.GetType() != typeof(ItemInventory))
        {
            foreach (Item child in inventory.AllItems) { child.UpdateSpriteStates(deltaTime); }
            return;
        }
        for (int slot = 0; slot < inventory.capacity; slot++)
        {
            IReadOnlyList<Item> items = inventory.slots[slot].Items;
            for (int index = 0; index < items.Count; index++)
            {
                Item child = items[index];
                if (child != null) { child.UpdateSpriteStates(deltaTime); }
            }
        }
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
                if (start >= 0) { throw new InvalidOperationException("Multiple contained sprite loops"); }
                start = i - 1;
            }
            Validate(code, start);
            Label visit = generator.DefineLabel();
            var replacement = new[]
            {
                new CodeInstruction(OpCodes.Volatile) { labels = code[start].labels },
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(SpriteTraversalFeature), nameof(on))),
                new CodeInstruction(OpCodes.Brfalse, visit),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(typeof(Item), nameof(Item.OwnInventories))),
                new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(System.Collections.Immutable.ImmutableArray<ItemInventory>), "Length")),
                new CodeInstruction(OpCodes.Ldc_I4_2),
                new CodeInstruction(OpCodes.Bge, visit),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Item), "ownInventory")),
                new CodeInstruction(OpCodes.Brtrue, visit),
                new CodeInstruction(OpCodes.Br, code[start + 14].operand),
                new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label> { visit } },
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpriteTraversalFeature), nameof(UpdateContents)))
            };
            code.RemoveRange(start, 20);
            code.InsertRange(start, replacement);
            Label exit = generator.DefineLabel();
            code[start + replacement.Length].labels.Add(exit);
            Label body = generator.DefineLabel();
            code[0].labels.Add(body);
            code.InsertRange(0, EmptyPath(generator, body, exit));
            sites = 1;
            fallback = null;
            return code;
        }
        catch (InvalidOperationException e)
        {
            sites = 0;
            on = emptyOn = false;
            fallback = e.Message;
            return code;
        }
    }

    static IEnumerable<CodeInstruction> EmptyPath(ILGenerator generator, Label body, Label exit)
    {
        LocalBuilder sprites = generator.DeclareLocal(typeof(System.Collections.Immutable.ImmutableArray<ContainedItemSprite>));
        return new[]
        {
            new CodeInstruction(OpCodes.Volatile),
            new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(SpriteTraversalFeature), nameof(emptyOn))),
            new CodeInstruction(OpCodes.Brfalse, body),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Item), "activeContainedSprite")),
            new CodeInstruction(OpCodes.Brtrue, body),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Item), nameof(Item.Prefab))),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(ItemPrefab), nameof(ItemPrefab.ContainedSprites))),
            new CodeInstruction(OpCodes.Stloc, sprites),
            new CodeInstruction(OpCodes.Ldloca, sprites),
            new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(sprites.LocalType, "IsEmpty")),
            new CodeInstruction(OpCodes.Brfalse, body),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Item), nameof(Item.Prefab))),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(ItemPrefab), nameof(ItemPrefab.DecorativeSpriteGroups))),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(System.Collections.Immutable.ImmutableDictionary<int, System.Collections.Immutable.ImmutableArray<DecorativeSprite>>), "Count")),
            new CodeInstruction(OpCodes.Brtrue, body),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(MapEntity), "Upgrades")),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(List<Upgrade>), "Count")),
            new CodeInstruction(OpCodes.Brtrue, body),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(typeof(Item), nameof(Item.OwnInventories))),
            new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(System.Collections.Immutable.ImmutableArray<ItemInventory>), "Length")),
            new CodeInstruction(OpCodes.Ldc_I4_2),
            new CodeInstruction(OpCodes.Bge, body),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Item), "ownInventory")),
            new CodeInstruction(OpCodes.Brtrue, body),
            new CodeInstruction(OpCodes.Br, exit)
        };
    }

    static void Validate(List<CodeInstruction> code, int start)
    {
        if (start < 0 || start + 20 >= code.Count) { throw new InvalidOperationException("Missing contained sprite loop"); }
        string[] shape = "ldarg.0 call callvirt stloc br ldloc callvirt stloc ldloc ldarg.1 callvirt ldloc callvirt brtrue leave ldloc brfalse ldloc callvirt endfinally".Split(' ');
        for (int i = 0; i < shape.Length; i++)
        {
            if (Shape(code[start + i]) != shape[i]) { throw new InvalidOperationException("Contained sprite loop instructions changed"); }
        }
        int cursor = Local(code[start + 3]), child = Local(code[start + 7]);
        var internalLabels = new HashSet<Label>();
        for (int i = start + 1; i < start + 20; i++) { internalLabels.UnionWith(code[i].labels); }
        int blocks = 0;
        for (int i = 0; i < code.Count; i++)
        {
            CodeInstruction instruction = code[i];
            bool inside = i >= start && i < start + 20;
            if (!inside)
            {
                if (Shape(instruction) is "ldloc" or "stloc" or "ldloca")
                {
                    int local = Local(instruction);
                    if (local == cursor || local == child) { throw new InvalidOperationException("Contained sprite loop local escapes"); }
                }
                if (instruction.operand is Label label && internalLabels.Contains(label)) { throw new InvalidOperationException("External branch enters contained sprite loop"); }
                if (instruction.operand is Label[] labels)
                {
                    foreach (Label target in labels)
                    {
                        if (internalLabels.Contains(target)) { throw new InvalidOperationException("External switch enters contained sprite loop"); }
                    }
                }
                continue;
            }
            int offset = i - start;
            if (Shape(instruction) == "ldloc" && Local(instruction) != (offset == 8 ? child : cursor))
            {
                throw new InvalidOperationException("Contained sprite loop data flow changed");
            }
            foreach (ExceptionBlock block in instruction.blocks)
            {
                ExceptionBlockType expected = blocks switch
                {
                    0 => ExceptionBlockType.BeginExceptionBlock,
                    1 => ExceptionBlockType.BeginFinallyBlock,
                    2 => ExceptionBlockType.EndExceptionBlock,
                    _ => throw new InvalidOperationException("Extra contained sprite exception region")
                };
                if (block.blockType != expected || offset != (blocks == 0 ? 4 : blocks == 1 ? 15 : 19))
                {
                    throw new InvalidOperationException("Contained sprite exception region changed");
                }
                blocks++;
            }
        }
        if (blocks != 3 ||
            !code[start + 2].Calls(AccessTools.Method(typeof(IEnumerable<Item>), nameof(IEnumerable<Item>.GetEnumerator))) ||
            !code[start + 6].Calls(AccessTools.PropertyGetter(typeof(IEnumerator<Item>), nameof(IEnumerator<Item>.Current))) ||
            !code[start + 10].Calls(update) ||
            !code[start + 12].Calls(AccessTools.Method(typeof(IEnumerator), nameof(IEnumerator.MoveNext))) ||
            !code[start + 18].Calls(AccessTools.Method(typeof(IDisposable), nameof(IDisposable.Dispose))) ||
            !BranchesTo(code, start + 4, start + 11) || !BranchesTo(code, start + 13, start + 5) ||
            !BranchesTo(code, start + 14, start + 20) || !BranchesTo(code, start + 16, start + 19))
        {
            throw new InvalidOperationException("Contained sprite loop control flow changed");
        }
    }

    static bool BranchesTo(List<CodeInstruction> code, int from, int to) =>
        code[from].operand is Label label && code[to].labels.Contains(label);

    static string Shape(CodeInstruction instruction)
    {
        string name = instruction.opcode.Name;
        if (name.StartsWith("ldloca", StringComparison.Ordinal)) { return "ldloca"; }
        if (name.StartsWith("ldloc", StringComparison.Ordinal)) { return "ldloc"; }
        if (name.StartsWith("stloc", StringComparison.Ordinal)) { return "stloc"; }
        return name.EndsWith(".s", StringComparison.Ordinal) ? name.Substring(0, name.Length - 2) : name;
    }

    static int Local(CodeInstruction instruction)
    {
        OpCode op = instruction.opcode;
        if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) { return 0; }
        if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) { return 1; }
        if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) { return 2; }
        if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) { return 3; }
        return instruction.operand is LocalBuilder local ? local.LocalIndex : Convert.ToInt32(instruction.operand);
    }

    internal override void Uninstall() => on = emptyOn = false;

    public override string Report() => $"active={on} loops={sites} emptyVisual={emptyOn}" +
        (observers != null ? " observed: " + observers : "") +
        (fallback != null ? " fallback: " + fallback : "");
}
