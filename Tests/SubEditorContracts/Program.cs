using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using Barotrauma;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.SubEditor;

[assembly: IgnoresAccessChecksTo("DedicatedServer")]
[assembly: IgnoresAccessChecksTo("BarotraumaCore")]

internal static class Program
{
    static int checks, drops;
    static void Check(bool value, string name)
    {
        checks++;
        if (!value) { throw new InvalidOperationException(name); }
    }

    static int Main(string[] args)
    {
        string game = Path.GetFullPath(args.Length > 0 ? args[0] : "../../../..");
        string server = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(game, "../../workshop/content/602960/2559634234/Binary"));
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(name.Name == "DedicatedServer" ? server : game, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            Run();
            Console.WriteLine($"SubEditorContracts: {checks} checks, 0 failures; native physics, no game loop");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        GameMain.World = new World(new Vector2(0, -9.82f));
        var harmony = new Harmony("primary.subeditor.world.contracts");
        ExactIds.Attach(harmony);
        SubIds.Attach(harmony);
        SubRegistry.Attach(harmony);
        harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Drop)),
            prefix: new HarmonyMethod(typeof(Program), nameof(Dropping)));
        var sub = new Submarine(new SubmarineInfo(), showErrorMessages: false);
        Submarine.MainSub = sub;
        var hulls = EntityOps.LoadAt(System.Text.Encoding.UTF8.GetBytes("<Entities><Hull ID='1' rect='-128,128,256,256'/></Entities>"), sub, 32000);
        Check(hulls is { Count: 1 }, "native hull loaded");
        EntityGrid removalGrid = new(new Rectangle(hulls[0].Rect.X - 128, hulls[0].Rect.Y + 128, 1024, 1024), 64);
        Rectangle originalRect = hulls[0].Rect;
        removalGrid.InsertEntity(hulls[0]);
        hulls[0].Rect = new Rectangle(originalRect.X + 128, originalRect.Y, originalRect.Width, originalRect.Height);
        try
        {
            removalGrid.RemoveEntity(hulls[0]);
            var cells = (List<MapEntity>[,])AccessTools.Field(typeof(EntityGrid), "entities").GetValue(removalGrid);
            bool anyStale = false;
            foreach (List<MapEntity> cell in cells)
            {
                if (cell.Contains(hulls[0])) { anyStale = true; break; }
            }
            Check(!anyStale && !removalGrid.GetAllEntities().Contains(hulls[0]), "moving entity removal leaves no stale grid cells");
        }
        finally { hulls[0].Rect = originalRect; }
        var collisionBody = sub.SubBody?.Body?.FarseerBody;
        Check(collisionBody != null, "native submarine collision body exists");
        bool initiallyEnabled = collisionBody.Enabled;
        int originalFixtureCount = collisionBody.FixtureList.Count;
        LiveGeometry.Rebuild(sub);
        Check(ReferenceEquals(collisionBody, sub.SubBody.Body.FarseerBody), "geometry rebuild preserves native submarine body identity");
        Check(collisionBody.Enabled == initiallyEnabled, "geometry rebuild restores body collision activation");
        Check(collisionBody.FixtureList.Count == originalFixtureCount, "geometry rebuild does not accumulate duplicate fixtures");
        using (LiveGeometry.Enter())
        {
            LiveGeometry.Touch(hulls[0]);
            LiveGeometry.Touch(hulls[0]);
        }
        Check(collisionBody.FixtureList.Count == originalFixtureCount, "coalesced geometry mutation retains stable fixture count");
        Check(!FrameApplier.ApplyTransaction(OpKind.Transform, new[] { hulls[0].ID }, Array.Empty<byte>()),
            "invalid transform cannot enter committed world state");
        Check(collisionBody.FixtureList.Count == originalFixtureCount && ReferenceEquals(collisionBody, sub.SubBody.Body.FarseerBody),
            "rejected world transaction preserves original collision body");
        Check(!WorldMutationCoordinator.Applying && !WorldMutationCoordinator.RollingBack,
            "transaction coordinator restores nesting state after failed validation");
        var file = (ItemFile)RuntimeHelpers.GetUninitializedObject(typeof(ItemFile));
        ItemPrefab Container(string identifier) => new ItemPrefab(XElement.Parse($"<Item name='{identifier}' identifier='{identifier}'><ItemContainer capacity='8'/></Item>").FromPackage(null), file);
        var box = Container("contractbox");
        var plain = new ItemPrefab(XElement.Parse("<Item name='contractplain' identifier='contractplain'/>").FromPackage(null), file);
        var physical = new ItemPrefab(XElement.Parse("<Item name='contractbody' identifier='contractbody'><Body width='16' height='16'/></Item>").FromPackage(null), file);
        foreach (ItemPrefab prefab in new[] { box, plain, physical }) { ItemPrefab.Prefabs.Add(prefab, isOverride: false); }
        GunfireCounts(hulls.OfType<Hull>().ToList());
        SnapshotContainment(sub, rect: new Rectangle((int)sub.HiddenSubPosition.X, (int)sub.HiddenSubPosition.Y, 16, 16), box, plain);
        Rectangle rect = new((int)sub.HiddenSubPosition.X, (int)sub.HiddenSubPosition.Y, 16, 16);
        var parent = new Item(rect, box, sub, id: 13000);
        var nested = new Item(rect, box, sub, id: 12500);
        var child = new Item(rect, plain, sub, id: 12000);
        Check(EntityOps.ApplyPlacement(nested, new InventoryPlacement(parent.ID, 0, 0)), "nested container placed");
        Check(EntityOps.ApplyPlacement(child, new InventoryPlacement(nested.ID, 0, 0)), "lower-ID child placed");
        var closure = EntityOps.WithContents(new ushort[] { parent.ID, child.ID, parent.ID });
        Check(closure.Select(entity => entity.ID).SequenceEqual(new ushort[] { 12000, 12500, 13000 }), "closure retains ascending IDs without duplicates");
        byte[] clipboard = EntityOps.SaveXml(closure);
        drops = 0;
        EntityOps.Delete(new ushort[] { parent.ID, child.ID, parent.ID });
        Check(drops == 0, "delete removes children before containers without native Drop events");
        Check(parent.Removed && nested.Removed && child.Removed, "nested delete removes complete ownership tree synchronously");
        var restored = EntityOps.LoadExact(clipboard, sub);
        Check(restored is { Count: 3 }, "complex clipboard XML restores every entity under its exact ID");
        Check((EntityOps.Find(12000) as Item)?.Container?.ID == 12500 && (EntityOps.Find(12500) as Item)?.Container?.ID == 13000,
            "clipboard restores nested inventory ownership independent of ID order");
        parent = (Item)EntityOps.Find(13000);
        var dropped = new Item(rect, physical, sub, id: 14000);
        Check(dropped.body != null && EntityOps.ApplyPlacement(dropped, new InventoryPlacement(parent.ID, 0, 1)), "physical item enters container");
        Vector2 outside = sub.WorldPosition + new Vector2(100000, -100000);
        InventoryPlacement placement = InventoryOps.CaptureDrop(dropped, outside);
        Check(placement.SubmarineId == SubRegistry.WorldId, "cursor outside hulls selects world frame");
        Check(InventoryOps.Apply(new[] { dropped.ID }, new[] { placement }), "apply overboard placement");
        Check(dropped.ParentInventory == null && dropped.Submarine == null && dropped.body.Submarine == null, "overboard item and native body share world ownership");
        Check(Vector2.Distance(dropped.WorldPosition, outside) < 2f, "overboard item center matches cursor without submarine offset");
        Check(InventoryOps.Apply(new[] { dropped.ID }, new[] { new InventoryPlacement(parent.ID, 0, 1) }), "undo restores inventory slot");
        Check(dropped.Container == parent, "undo restores container ownership");
        placement = InventoryOps.CaptureDrop(dropped, sub.WorldPosition);
        Check(placement.SubmarineId == SubRegistry.WireId(sub), "cursor inside hull selects its submarine frame");
        var bulk = new ushort[2048];
        for (int i = 0; i < bulk.Length; i++) { bulk[i] = new Item(rect, plain, sub).ID; }
        EntityOps.Delete(bulk);
        Check(bulk.All(id => EntityOps.Find(id) == null), "bulk delete releases 2048 native entities synchronously");
        EntityOps.Delete(new[] { parent.ID });
        EntityOps.Delete(hulls.Select(hull => hull.ID).ToArray());
        harmony.UnpatchSelf();
    }

    static void Dropping() => drops++;

    static void SnapshotContainment(Submarine sub, Rectangle rect, ItemPrefab box, ItemPrefab plain)
    {
        var parent = new Item(rect, box, sub, id: 21000);
        var survivor = new Item(rect, plain, sub, id: 21001);
        byte[] looseState = EntityOps.CaptureStateBytes(survivor);
        byte[] parentXml = EntityOps.SaveXml(new MapEntity[] { parent });
        Check(EntityOps.ApplyPlacement(survivor, new InventoryPlacement(parent.ID, 0, 0)), "fixture has stale local containment before authoritative removal");
        drops = 0;
        EntityOps.DeleteExact(new[] { parent.ID });
        Check(parent.Removed && !survivor.Removed && EntityOps.Find(survivor.ID) == survivor && survivor.ParentInventory == null,
            "Gone snapshot preserves an InPlace survivor from a stale container");
        Check(drops == 0, "snapshot detaches survivors without native Drop effects");
        Check(EntityOps.RestoreStates(new[] { survivor.ID }, new[] { looseState }), "InPlace state restores after Gone container reconciliation");
        parent = EntityOps.LoadExact(parentXml, sub)?.Single() as Item;
        Check(parent != null && EntityOps.ApplyPlacement(survivor, new InventoryPlacement(parent.ID, 0, 0)), "fixture recreates stale containment before Full replacement");
        EntityOps.DeleteExact(new[] { parent.ID });
        Check(!survivor.Removed && EntityOps.LoadExact(parentXml, sub) is { Count: 1 }, "Full replacement preserves existing children while rebuilding their container ID");
        Check(EntityOps.RestoreStates(new[] { survivor.ID }, new[] { looseState }) && survivor.ParentInventory == null,
            "Full plus InPlace snapshot restores authoritative loose placement");
        EntityOps.Delete(new ushort[] { 21000, 21001 });

        parent = new Item(rect, box, sub, id: 22000);
        var nested = new Item(rect, box, sub, id: 22001);
        var doomed = new Item(rect, plain, sub, id: 22002);
        Check(EntityOps.ApplyPlacement(nested, new InventoryPlacement(parent.ID, 0, 0)) &&
            EntityOps.ApplyPlacement(doomed, new InventoryPlacement(nested.ID, 0, 0)), "mixed ownership fixture is nested");
        EntityOps.DeleteExact(new ushort[] { 22000, 22002 });
        Check(parent.Removed && doomed.Removed && !nested.Removed && nested.ParentInventory == null,
            "exact snapshot set removes a selected descendant while preserving its unselected owner");
        EntityOps.Delete(new[] { nested.ID });
    }

    static void GunfireCounts(List<Hull> hulls)
    {
        Type scenarios = typeof(EntityOps).Assembly.GetType("IEngine.Diagnostics.Scenarios", throwOnError: true);
        Type gunfire = scenarios.GetNestedType("Gunfire", BindingFlags.NonPublic);
        AccessTools.Field(scenarios, "hulls").SetValue(null, hulls);
        object[] arguments = { 8, 12f, "contractplain", 6, 30f, 0, false };
        object scenario = Activator.CreateInstance(gunfire, arguments);
        AccessTools.Method(gunfire, "Start").Invoke(scenario, null);
        Check(((List<Vector2>)AccessTools.Field(gunfire, "shooters").GetValue(scenario)).Count == 8,
            "one hull still produces all 8 requested firing positions");
        Check(((List<Vector2>)AccessTools.Field(gunfire, "targetSpots").GetValue(scenario)).Count == 6,
            "target positions retain requested count independently of hull count");
        arguments[1] = float.PositiveInfinity;
        bool rejected = false;
        try { Activator.CreateInstance(gunfire, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "nonfinite rate cannot create an endless shot-budget loop");
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}
