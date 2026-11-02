using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using PrimaryPackage.Interactions;
using PrimaryPackage.GUI;
using PrimaryPackage.Emotions;

internal static partial class Program {
    private static int checks;
    private static readonly List<string> failures = new();
    private static readonly SceneBindings binding = new("player", 1, 2, 0, 0, 0, 0, 500);
    private sealed class World : ISceneWorld {
        internal bool Valid = true, Condition, Throw;
        internal int Observations, Disposed;
        internal readonly List<SceneEmission> Emitted = new();
        internal readonly HashSet<Guid> Cancelled = new();
        public ISceneLease Bind(string owner, SceneProgram program, in SceneBindings bindings) => new Lease(this);
        private sealed class Lease : ISceneLease {
            private readonly World owner;
            internal Lease(World owner) => this.owner = owner;
            public SceneObservation Observe() { owner.Observations++; return new(owner.Valid, owner.Condition); }
            public void Execute(in SceneEmission emission) { if (owner.Throw) { throw new InvalidOperationException("device removed"); } owner.Emitted.Add(emission); }
            public void Cancel(in SceneEmission emission) => owner.Cancelled.Add(emission.Id);
            public void Dispose() => owner.Disposed++;
        }
    }
    private static void Check(bool pass, string name) { checks++; if (!pass) { failures.Add(name); } }
    private static void Near(double actual, double expected, double error, string name) => Check(Math.Abs(actual - expected) <= error, name + $" ({actual:R}, expected {expected:R})");
    private static void Reject<T>(Action action, string name) where T : Exception { try { action(); Check(false, name); } catch (T) { Check(true, name); } }
    private static SceneProgram Scene(SceneTrigger trigger = SceneTrigger.Manual) => new() { Name = "Door scene", Trigger = trigger, Steps = new() { new() { Action = SceneAction.Caption, Text = "A", Duration = 1 }, new() { Action = SceneAction.Caption, Text = "B", Delay = 2, Duration = 1 } } };
    private static void Scenes() {
        var world = new World { Condition = true }; var runtime = new SceneRuntime(); var program = Scene(SceneTrigger.DoorOpened); Guid id = Guid.NewGuid();
        runtime.Arm(id, 0, "gm1", program, binding, 0, world);
        runtime.Advance(1); Check(world.Emitted.Count == 0, "an already open door must not fire on arm");
        world.Condition = false; runtime.Advance(2); world.Condition = true; runtime.Advance(3);
        Check(world.Emitted.Count == 1 && world.Emitted[0].Step.Text == "A", "door rising edge emits first step");
        program.Steps[1].Text = "external mutation";
        runtime.Advance(5); Check(world.Emitted.Count == 2 && world.Emitted[1].Step.Text == "B", "armed program owns a snapshot of the author draft");
        Check(world.Emitted[0].Id != world.Emitted[1].Id, "step identifiers are independent");
        Reject<InvalidOperationException>(() => runtime.Arm(id, 0, "gm2", program, binding, 5, world), "stale GM update must be rejected");
        Reject<InvalidOperationException>(() => runtime.Remove(id, 2), "stale GM cancellation must be rejected");
        runtime.Remove(id, 1); Check(world.Cancelled.Count == 2 && world.Disposed == 1, "cancel releases effects and pinned world lease");
        world = new World(); runtime = new SceneRuntime(); var ordered = Scene(); ordered.Steps[0].Delay = 2; ordered.Steps[1].Delay = 0;
        ordered.Steps.Add(new SceneStep { Action = SceneAction.Caption, Text = "C", Delay = 0, Duration = 1 });
        runtime.Arm(Guid.NewGuid(), 0, "gm", ordered, binding, 0, world); runtime.Advance(0);
        Check(world.Emitted.Count == 2 && world.Emitted[0].Step.Text == "B" && world.Emitted[1].Step.Text == "C", "sort by time preserves equal-time authored order");
        world.Valid = false; runtime.Advance(1);
        Check(runtime.Entries[0].State == SceneState.Faulted && world.Cancelled.Count == 2, "permission revocation cancels effects and pending actions");
        runtime.Advance(5); Check(world.Emitted.Count == 2, "faulted scene never emits pending actions");
        Reject<ArgumentOutOfRangeException>(() => runtime.Advance(4), "backward scene clock rejected");
        runtime.Clear(); Check(runtime.Entries.Count == 0 && world.Disposed == 1, "round cleanup owns all leases");
        world = new World(); runtime = new SceneRuntime(); var repeat = Scene(SceneTrigger.EnterArea); repeat.Repeat = true; repeat.Cooldown = 2; repeat.Steps.RemoveAt(1);
        runtime.Arm(Guid.NewGuid(), 0, "gm", repeat, binding, 0, world); world.Condition = true; runtime.Advance(1); runtime.Advance(2);
        world.Condition = false; runtime.Advance(2.1); world.Condition = true; runtime.Advance(3);
        Check(world.Emitted.Count == 1, "cooldown rejects an early new entry");
        runtime.Advance(5); Check(world.Emitted.Count == 1, "remaining in area cannot retrigger after cooldown");
        world.Condition = false; runtime.Advance(6); world.Condition = true; runtime.Advance(7);
        Check(world.Emitted.Count == 2 && world.Emitted[0].Id != world.Emitted[1].Id, "new entry after cooldown gets a fresh effect identity");
        world = new World(); runtime = new SceneRuntime(); var failed = Scene(); runtime.Arm(Guid.NewGuid(), 0, "gm", failed, binding, 0, world); runtime.Advance(0); world.Throw = true; runtime.Advance(2);
        Check(runtime.Entries[0].State == SceneState.Faulted && world.Cancelled.Count == 1, "device failure cancels previously emitted effects");
        world = new World(); runtime = new SceneRuntime(); runtime.Arm(Guid.NewGuid(), 0, "gm", Scene(), binding, 0, world); runtime.Advance(0);
        Check(runtime.FailEffect(world.Emitted[0].Id, "Client resource unavailable"), "target client failure resolves back to its scene");
        Check(runtime.Entries[0].State == SceneState.Faulted && world.Cancelled.Count == 1, "target client failure cancels future world actions");
        world = new World { Valid = false }; runtime = new SceneRuntime();
        Reject<ArgumentException>(() => runtime.Arm(Guid.NewGuid(), 0, "gm", Scene(), binding, 0, world), "invalid binding rejected before registration");
        Check(runtime.Entries.Count == 0 && world.Disposed == 1, "failed registration releases new lease");
        world = new World(); for (int i = 0; i < SceneRuntime.Capacity; i++) { runtime.Arm(Guid.NewGuid(), 0, "gm", Scene(SceneTrigger.EnterArea), binding, 0, world); }
        Reject<InvalidOperationException>(() => runtime.Arm(Guid.NewGuid(), 0, "gm", Scene(), binding, 0, world), "server scene capacity is enforced");
        runtime = new SceneRuntime(); world = new World(); Guid reused = Guid.NewGuid();
        var old = runtime.Arm(reused, 0, "gm1", Scene(), binding, 0, world); uint oldRevision = old.Revision;
        runtime.Remove(reused, oldRevision); runtime.Arm(reused, 0, "gm2", Scene(), binding, 0, world);
        Reject<InvalidOperationException>(() => runtime.Remove(reused, oldRevision), "deleted and recreated scene cannot accept an old GM revision");
        var invalidAudience = Scene(); invalidAudience.Steps[0].Action = SceneAction.Hallucination; invalidAudience.Steps[0].Audience = SceneAudience.Everyone;
        Reject<ArgumentException>(invalidAudience.Validate, "personal hallucination can never request broadcast audience");
        var invalid = Scene(); invalid.Steps[0].Duration = float.NaN; Reject<ArgumentException>(invalid.Validate, "nonfinite scene inputs rejected");
        invalid = Scene(); invalid.Steps[0].Text = "<tag>"; Reject<ArgumentException>(invalid.Validate, "scene text cannot inject rich text");
    }
    private static void Phantoms() {
        var a = new PhantomMotion(Vector2.Zero, 500, 80, PhantomBehavior.Pursue, 0);
        var b = new PhantomMotion(Vector2.Zero, 500, 80, PhantomBehavior.Pursue, 0);
        for (int i = 1; i <= 60; i++) { a.Advance(i / 30.0, new Vector2(1000, 0), .6f); }
        for (int i = 1; i <= 288; i++) { b.Advance(i / 144.0, new Vector2(1000, 0), .6f); }
        Near(a.Position.X, 160, .001, "phantom speed is in world units per second"); Near(a.Position.X, b.Position.X, .001, "phantom trajectory independent of render FPS");
        var held = new PhantomMotion(Vector2.Zero, 100, 80, PhantomBehavior.Lagged, 0); held.Advance(.02, new Vector2(1000, 0), 1);
        Check(held.Position.X > 0 && held.Displayed == Vector2.Zero, "lag effect holds only presentation while simulation advances");
        held.Advance(.25, new Vector2(1000, 0), 1); Check(held.Displayed == held.Position && held.Ghost == Vector2.Zero, "held presentation advances with previous ghost");
        held.Advance(10, new Vector2(1000, 0), 1); Near(held.Position.Length(), 100, .001, "phantom remains within bound area");
        var vanish = new PhantomMotion(Vector2.Zero, 500, 80, PhantomBehavior.VanishOnApproach, 0); vanish.Advance(.1, new Vector2(50, 0), .5f); Check(vanish.Vanished, "phantom proximity disappearance");
        Reject<ArgumentException>(() => held.Advance(9, Vector2.Zero, .5f), "phantom rejects backward time");
    }
    private static void Motion() {
        foreach (MotionCurve curve in new[] { MotionCurve.Linear, MotionCurve.Smooth, MotionCurve.EaseOut, new MotionCurve(0, 0, 0, 1), new MotionCurve(1, 0, 1, 1) }) {
            double previous = -1;
            for (int i = 0; i <= 1000; i++) {
                curve.Sample(i / 1000.0, out double p, out double v);
                Check(double.IsFinite(p) && double.IsFinite(v) && p >= previous - 1e-10 && p >= -1e-10 && p <= 1 + 1e-10 && v >= -1e-10, "curve finite monotone bounded sample"); previous = p;
                if (i is > 10 and < 990) { curve.Sample(i / 1000.0 - 1e-6, out double before, out _); curve.Sample(i / 1000.0 + 1e-6, out double after, out _); Near(v, (after - before) / 2e-6, .001, "curve analytic speed matches displacement derivative"); }
            }
        }
        var scheduler = new GUIMotion(); var value = scheduler.Create(0, MotionSettings.Open); value.AnimateTo(1, 2, MotionCurve.Smooth);
        scheduler.Advance(1); Near(value.Position, .5, 1e-10, "timed animation midpoint"); scheduler.Advance(2); Check(value.Position == 1 && value.Velocity == 0 && !value.Moving, "timed animation settles at exact deadline");
        value.MoveTo(0); scheduler.Advance(2.1); double p0 = value.Position, v0 = value.Velocity; value.MoveTo(2); Near(value.Position, p0, 1e-12, "spring retarget position continuity"); Near(value.Velocity, v0, 1e-12, "spring retarget velocity continuity");
        value.Dispose(); Check(scheduler.ActiveCount == 0, "disposed animation leaves scheduler");
    }
    private static void Emotions(string root) {
        var catalog = EmoteCatalog.Parse(File.ReadAllText(Path.Combine(root, "Content/Systems/RPWheel/Definitions/emotes.xml")), File.ReadAllText(Path.Combine(root, "Content/Systems/RPWheel/Emotions/Sounds/manifest.json")));
        Check(catalog.Count > 12 && catalog.Problems.Count == 0, "imported emotion catalog and audio manifest parse without loss");
        foreach (var definition in catalog.All) {
            for (int variant = 0; variant < Math.Max(1, definition.Sounds.Length); variant++) {
                var right = new HandTrack(); var left = new HandTrack();
                Poses.Build(definition.Pose, definition.Duration(variant), definition.Sounds.Length == 0 ? Array.Empty<float>() : definition.Sounds[variant].Beats,
                    new PoseFrame { Arm = .7f, Head = new Vector2(.05f, .3f), RightStart = new Vector2(.1f, -.6f), LeftStart = new Vector2(.1f, -.6f) }, right, left);
                for (int t = 0; t <= 100; t++) {
                    right.Sample(Poses.Total(definition.Pose, definition.Duration(variant)) * t / 100, out var position, out var speed);
                    Check(float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(speed.X) && float.IsFinite(speed.Y), "authored emotion trajectory remains finite");
                }
            }
        }
        foreach (var pose in Enum.GetValues<EmotePose>()) {
            Poses.Build(pose, 1, new[] { 1f }, new PoseFrame { Arm = .7f }, new HandTrack(), new HandTrack());
            Check(true, "sound ending on final impact preserves chronological keys");
        }
        var hands = catalog.Find("clap"); var facts = new EmoteFacts { Alive = true, Conscious = true, SinceLast = 100, Vitality = 1, AnyHandBusy = true };
        Check(EmoteRules.Check(hands, facts) == EmoteDeny.HandsBusy, "two-handed gesture rejected with one occupied hand"); facts.AnyHandBusy = false; facts.Bound = true;
        Check(EmoteRules.Check(hands, facts) == EmoteDeny.Bound, "bound gesture rejected"); facts.Bound = false; facts.Emoting = true;
        Check(EmoteRules.Check(hands, facts) == EmoteDeny.Busy, "overlapping emotions rejected");
        var path = new HandTrack(); path.Key(0, Vector2.Zero); path.Key(1, Vector2.One); path.Key(1, new Vector2(2)); Check(path.Count == 2, "coincident hand key replaces authored target without shifting time");
        Reject<ArgumentException>(() => path.Key(.5f, Vector2.Zero), "backward hand key rejected");
    }
    private static void Migration() {
        var old = new List<LegacyDirectorScene> { new() { Name = "Old", Events = new() {
            new() { Kind = 0, Target = "player", Resource = "sound", Duration = 4, Interval = 1.5f, Delay = 2 },
            new() { Kind = 2, Text = "All players", Duration = 6 }
        } } };
        var migrated = LegacyScenes.Convert(old);
        Check(migrated[0].Steps[0].Action == SceneAction.Sound && migrated[0].Steps[0].Interval == 1.5f && migrated[0].Steps[0].Delay == 2, "legacy sound timing and repeat interval survive migration");
        Check(migrated[0].Steps[1].Audience == SceneAudience.Everyone, "legacy broadcast caption retains its audience");
        old[0].Events.Add(new LegacyDirectorEffect { Kind = 0, Target = "another", Resource = "sound" });
        Reject<ArgumentException>(() => LegacyScenes.Convert(old), "unsupported legacy multi-target scene is reported instead of silently retargeted");
    }
    private static void Leases() {
        var leases = new TimedLeases<object>(2); var lamp = new object(); var other = new object(); Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        leases.Start(a, lamp, 10); leases.Start(b, lamp, 20);
        Check(leases.TryGetEnd(lamp, out double end) && end == 20, "overlapping GM light effects share their maximum live end");
        leases.Cancel(b); Check(leases.TryGetEnd(lamp, out end) && end == 10, "cancelling one GM preserves the other light effect");
        leases.Start(b, lamp, 20); leases.Expire(10); Check(leases.TryGetEnd(lamp, out end) && end == 20, "expiry of one light lease preserves its neighbor");
        Reject<ArgumentException>(() => leases.Start(b, other, 30), "lease cannot migrate to another component");
        Check(!leases.TryGetEnd(other, out _), "failed lease mutation leaves no derived target entry");
        leases.Expire(20); Check(!leases.TryGetEnd(lamp, out _), "last lease expiration clears the derived light state");
        leases.Start(a, lamp, 30); leases.Clear(); Check(!leases.TryGetEnd(lamp, out _) && leases.Entries.Count == 0, "round end releases both lease state and derived index");
    }
    private static object Measure() {
        var world = new World(); var runtime = new SceneRuntime();
        for (int i = 0; i < SceneRuntime.Capacity; i++) { runtime.Arm(Guid.NewGuid(), 0, "gm", Scene(SceneTrigger.EnterArea), binding, 0, world); }
        for (int i = 0; i < 1000; i++) { runtime.Advance(i); }
        long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
        const int samples = 100000;
        for (int i = 0; i < samples; i++) { runtime.Advance(1000 + i); }
        long ticks = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(bytes == 0, "steady scene polling allocates zero bytes in the measured pure runtime");
        return new { scenario = "32 armed scenes, fake world, no dispatch", samples, meanMicroseconds = ticks * 1e6 / Stopwatch.Frequency / samples, allocatedBytes = bytes, worldObservations = world.Observations, scope = "pure CPU runtime only; no GPU, game, serialization or network measurement" };
    }
    private static int Main(string[] args) {
        if (args.Length == 3 && args[0] == "--contracts") {
            try { Console.WriteLine(JsonSerializer.Serialize(Contracts.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        }
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        try { Scenes(); Phantoms(); Motion(); Emotions(root); Leases(); Migration(); WorldEditing(); PickingIndex(); HorrorSignals(); NetworkBits(); object networkChecks = NetworkTransfers(); object networkMeasurement = MeasureNetworkBits(); object editorMeasurement = MeasureEditor(); object measurement = Measure(); Console.WriteLine(JsonSerializer.Serialize(new { checks, failed = failures.Count, failures, measurement, editorMeasurement, networkChecks, networkMeasurement }, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        return failures.Count == 0 ? 0 : 1;
    }
}
