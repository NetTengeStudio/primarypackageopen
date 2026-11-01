using System;
using System.Collections.Generic;

namespace PrimaryPackage.Interactions;

public enum SceneTrigger : byte { Manual, DoorOpened, EnterArea }
public enum SceneAudience : byte { SelectedPlayer, Everyone }
public enum SceneAction : byte { Sound, Scare, Caption, Hallucination, Flicker, OpenDoor, CloseDoor, SignalCollapse }
public enum PhantomBehavior : byte { Watch, Approach, Pursue, Retreat, VanishOnApproach, Lagged }

public sealed class SceneStep {
    public SceneAction Action { get; set; }
    public SceneAudience Audience { get; set; }
    public float Delay { get; set; }
    public float Duration { get; set; } = 5;
    public float Interval { get; set; } = 2;
    public string Resource { get; set; } = "";
    public string Visual { get; set; } = "";
    public string Text { get; set; } = "";
    public float Gain { get; set; } = .35f;
    public float Intensity { get; set; } = .6f;
    public float Scale { get; set; } = 1;
    public float Speed { get; set; } = 80;
    public float NearDistance { get; set; } = 100;
    public PhantomBehavior Behavior { get; set; }
    public SceneStep Copy() => (SceneStep)MemberwiseClone();
}

public sealed class SceneProgram {
    public const int MaxSteps = 16;
    public string Name { get; set; } = "";
    public SceneTrigger Trigger { get; set; }
    public bool Repeat { get; set; }
    public float Cooldown { get; set; } = 5;
    public List<SceneStep> Steps { get; set; } = new();

    public void Validate() {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64 || !Enum.IsDefined(Trigger) ||
            !float.IsFinite(Cooldown) || Cooldown < 1 || Cooldown > 3600 || Steps == null || Steps.Count is < 1 or > MaxSteps) {
            throw new ArgumentException("Укажите название, условие и от 1 до 16 действий сцены");
        }
        foreach (char c in Name) { if (char.IsControl(c) || c is '<' or '>') { throw new ArgumentException("Недопустимое название сцены"); } }
        foreach (SceneStep step in Steps) {
            if (step == null || !Enum.IsDefined(step.Action) || !Enum.IsDefined(step.Audience) || step.Audience == SceneAudience.Everyone && step.Action != SceneAction.Caption || !Enum.IsDefined(step.Behavior) ||
                !FiniteRange(step.Delay, 0, 120) || !FiniteRange(step.Duration, .25f, 60) || !FiniteRange(step.Interval, .5f, 60) ||
                !FiniteRange(step.Gain, 0, .7f) || !FiniteRange(step.Intensity, 0, 1) || !FiniteRange(step.Scale, .1f, 4) || !FiniteRange(step.Speed, 0, 500) || !FiniteRange(step.NearDistance, 1, 5000) ||
                step.Resource == null || step.Resource.Length > 512 || step.Visual == null || step.Visual.Length > 128 ||
                step.Text == null || step.Text.Length > 512) { throw new ArgumentException("Недопустимые параметры действия сцены"); }
            foreach (char c in step.Text) { if (char.IsControl(c) || c is '<' or '>') { throw new ArgumentException("Недопустимый текст сцены"); } }
            if (step.Action == SceneAction.Caption && string.IsNullOrWhiteSpace(step.Text)) { throw new ArgumentException("Введите текст сообщения"); }
            if (step.Action == SceneAction.Flicker && step.Duration > 10) { throw new ArgumentException("Мерцание: не более 10 секунд"); }
        }
    }
    private static bool FiniteRange(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
    public SceneProgram Copy() {
        var copy = new SceneProgram { Name = Name, Trigger = Trigger, Repeat = Repeat, Cooldown = Cooldown, Steps = new List<SceneStep>(Steps.Count) };
        foreach (SceneStep step in Steps) { copy.Steps.Add(step.Copy()); }
        // List.Sort is not stable: preserve the GM's order for equal timestamps.
        for (int i = 1; i < copy.Steps.Count; i++) {
            SceneStep step = copy.Steps[i]; int j = i;
            while (j > 0 && copy.Steps[j - 1].Delay > step.Delay) { copy.Steps[j] = copy.Steps[j - 1]; j--; }
            copy.Steps[j] = step;
        }
        return copy;
    }
}

public readonly struct SceneBindings {
    public readonly string Target;
    public readonly ushort Door, Light, Submarine;
    public readonly byte LightComponent;
    public readonly float X, Y, Radius;
    public SceneBindings(string target, ushort door, ushort light, byte component, ushort submarine, float x, float y, float radius) {
        if (target == null || target.Length > 128 || !float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 1e7f || Math.Abs(y) > 1e7f ||
            !float.IsFinite(radius) || radius < 10 || radius > 5000) { throw new ArgumentException("Недопустимые привязки сцены"); }
        Target = target; Door = door; Light = light; LightComponent = component; Submarine = submarine; X = x; Y = y; Radius = radius;
    }
}

public readonly struct SceneObservation {
    public readonly bool Valid, Condition;
    public SceneObservation(bool valid, bool condition) { Valid = valid; Condition = condition; }
}

public enum SceneState : byte { Armed, Running, Completed, Faulted }
public readonly struct SceneEmission {
    public readonly Guid Id;
    public readonly SceneStep Step;
    public readonly SceneBindings Bindings;
    internal SceneEmission(Guid id, SceneStep step, in SceneBindings bindings) { Id = id; Step = step; Bindings = bindings; }
}

public interface ISceneWorld {
    ISceneLease Bind(string owner, SceneProgram program, in SceneBindings bindings);
}
public interface ISceneLease : IDisposable {
    SceneObservation Observe();
    void Execute(in SceneEmission emission);
    void Cancel(in SceneEmission emission);
}

// The clock and world are supplied by the caller. No engine collections or network state live here.
public sealed class SceneRuntime {
    public const int Capacity = 32;
    public sealed class Entry {
        public Guid Id { get; internal set; }
        public uint Revision { get; internal set; }
        public string Owner { get; internal set; }
        internal SceneProgram Program { get; set; }
        public string Name => Program.Name;
        public SceneTrigger Trigger => Program.Trigger;
        public SceneBindings Bindings { get; internal set; }
        public SceneState State { get; internal set; }
        public string Error { get; internal set; } = "";
        internal ISceneLease Lease;
        internal bool Previous;
        internal double Started, ReadyAt;
        internal int Cursor;
        internal readonly Guid[] Effects = new Guid[SceneProgram.MaxSteps];
    }
    private readonly Dictionary<Guid, Entry> entries = new(Capacity);
    private readonly List<Entry> ordered = new(Capacity);
    private double time;
    private uint revisionCounter;
    private bool advancing;
    public IReadOnlyList<Entry> Entries => ordered;
    public ulong Version { get; private set; }

    public Entry Arm(Guid id, uint expectedRevision, string owner, SceneProgram program, in SceneBindings bindings, double now, ISceneWorld world) {
        CheckClock(now);
        if (advancing) { throw new InvalidOperationException("Нельзя менять сцены во время исполнения"); }
        if (id == Guid.Empty || string.IsNullOrEmpty(owner) || world == null) { throw new ArgumentException("Недопустимый идентификатор сцены"); }
        program.Validate();
        if (entries.TryGetValue(id, out Entry previous)) {
            if (previous.Revision != expectedRevision) { throw new InvalidOperationException("Сцену уже изменил другой GM. Обновите список"); }

        } else if (expectedRevision != 0 || entries.Count >= Capacity) { throw new InvalidOperationException("Сцена удалена или достигнут предел 32 сцен"); }
        if (revisionCounter == uint.MaxValue) { throw new InvalidOperationException("Исчерпаны ревизии сцен: перезагрузите платформу"); }
        SceneProgram owned = program.Copy();
        ISceneLease lease = world.Bind(owner, owned, bindings) ?? throw new InvalidOperationException("Мир не предоставил привязки сцены");
        SceneObservation observation;
        try {
            observation = lease.Observe();
            if (!observation.Valid) { throw new ArgumentException("Проверьте права, игрока и привязанные объекты сцены"); }
        } catch { lease.Dispose(); throw; }
        var entry = new Entry { Id = id, Revision = ++revisionCounter, Owner = owner, Program = owned, Bindings = bindings, Lease = lease, Previous = observation.Condition, State = SceneState.Armed };
        if (previous != null) { CancelEffects(previous); previous.Lease.Dispose(); ordered[ordered.IndexOf(previous)] = entry; }
        else { ordered.Add(entry); }
        entries[id] = entry; Version++;
        if (owned.Trigger == SceneTrigger.Manual) { Begin(entry, now); }
        return entry;
    }
    public void Remove(Guid id, uint revision) {
        if (advancing) { throw new InvalidOperationException("Нельзя менять сцены во время исполнения"); }
        if (!entries.TryGetValue(id, out Entry entry) || entry.Revision != revision) { throw new InvalidOperationException("Сцена уже изменена. Обновите список"); }
        CancelEffects(entry); entry.Lease.Dispose(); entries.Remove(id); ordered.Remove(entry); Version++;
    }
    public bool FailEffect(Guid id, string reason) {
        if (advancing || id == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 160) { throw new ArgumentException("Недопустимое сообщение об эффекте"); }
        for (int i = 0; i < ordered.Count; i++) {
            Entry entry = ordered[i];
            if (entry.State != SceneState.Running) { continue; }
            for (int step = 0; step < entry.Cursor; step++) {
                if (entry.Effects[step] != id) { continue; }
                CancelEffects(entry); entry.State = SceneState.Faulted; entry.Error = reason; Version++; return true;
            }
        }
        return false;
    }
    public void Clear() {
        for (int i = 0; i < ordered.Count; i++) { CancelEffects(ordered[i]); ordered[i].Lease.Dispose(); }
        if (entries.Count > 0) { Version++; }
        entries.Clear(); ordered.Clear(); time = 0;
    }
    private void Begin(Entry entry, double now) {
        entry.Started = now; entry.Cursor = 0; entry.State = SceneState.Running; Version++;
        for (int i = 0; i < entry.Program.Steps.Count; i++) { entry.Effects[i] = Guid.NewGuid(); }
    }
    private static void CancelEffects(Entry entry) {
        for (int i = 0; i < entry.Cursor; i++) { entry.Lease.Cancel(new SceneEmission(entry.Effects[i], entry.Program.Steps[i], entry.Bindings)); }
        entry.Cursor = 0;
    }
    private void CheckClock(double now) {
        if (!double.IsFinite(now) || now < time) { throw new ArgumentOutOfRangeException(nameof(now), "Часы сцены должны быть монотонными"); }
        time = now;
    }
    public void Advance(double now) {
        CheckClock(now);
        if (advancing) { throw new InvalidOperationException("Недопустимое исполнение сцен"); }
        advancing = true;
        try {
            for (int i = 0; i < ordered.Count; i++) {
                Entry entry = ordered[i];
                if (entry.State is SceneState.Completed or SceneState.Faulted) { continue; }
                try {
                    SceneObservation observation = entry.Lease.Observe();
                    if (!observation.Valid) { throw new InvalidOperationException("Игрок, объект или полномочия больше недоступны"); }
                    bool entered = observation.Condition && !entry.Previous;
                    entry.Previous = observation.Condition;
                    if (entry.State == SceneState.Armed && entered && now >= entry.ReadyAt) { Begin(entry, now); }
                    if (entry.State != SceneState.Running) { continue; }
                    while (entry.Cursor < entry.Program.Steps.Count) {
                        SceneStep step = entry.Program.Steps[entry.Cursor];
                        if (entry.Started + step.Delay > now) { break; }
                        var emission = new SceneEmission(entry.Effects[entry.Cursor], step, entry.Bindings);
                        entry.Lease.Execute(emission);
                        entry.Cursor++;
                    }
                    if (entry.Cursor != entry.Program.Steps.Count) { continue; }
                    double end = entry.Started;
                    foreach (SceneStep step in entry.Program.Steps) { end = Math.Max(end, entry.Started + step.Delay + step.Duration); }
                    if (now < end) { continue; }
                    entry.State = entry.Program.Repeat && entry.Program.Trigger != SceneTrigger.Manual ? SceneState.Armed : SceneState.Completed;
                    Version++;
                    entry.ReadyAt = now + entry.Program.Cooldown;
                    entry.Cursor = 0;
                } catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException) {
                    CancelEffects(entry); entry.State = SceneState.Faulted; entry.Error = error.Message; Version++;
                }
            }
        } finally { advancing = false; }
    }
}
