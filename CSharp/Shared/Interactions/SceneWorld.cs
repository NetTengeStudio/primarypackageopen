using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Interactions;

internal sealed class SceneWorld : ISceneWorld {
    private readonly Func<string, Character> target;
    private readonly Func<string, InteractionAction, bool> allows;
    private readonly Action<DirectorEvent, string> publish;
    private readonly Action<Guid, Item, byte, float> light;
    internal SceneWorld(Func<string, Character> target, Func<string, InteractionAction, bool> allows, Action<DirectorEvent, string> publish, Action<Guid, Item, byte, float> light) {
        this.target = target; this.allows = allows; this.publish = publish; this.light = light;
    }
    public ISceneLease Bind(string owner, SceneProgram program, in SceneBindings bindings) {
        if (GameMain.GameSession?.IsRunning != true) { throw new InvalidOperationException("Раунд не запущен"); }
        return new Lease(this, owner, program, bindings);
    }

    private sealed class Lease : ISceneLease {
        private readonly SceneWorld world;
        private readonly string owner;
        private readonly SceneProgram program;
        private readonly SceneBindings bindings;
        private readonly Item doorItem, lightItem;
        private readonly Door door;
        private readonly Submarine submarine;
        private readonly bool needsDoor, needsLight, changesDoor, needsPlayer;
        private bool disposed;
        internal Lease(SceneWorld world, string owner, SceneProgram program, in SceneBindings bindings) {
            this.world = world; this.owner = owner; this.program = program; this.bindings = bindings;
            needsDoor = program.Trigger == SceneTrigger.DoorOpened;
            needsPlayer = program.Trigger == SceneTrigger.EnterArea;
            foreach (SceneStep step in program.Steps) {
                changesDoor |= step.Action is SceneAction.OpenDoor or SceneAction.CloseDoor;
                needsDoor |= changesDoor;
                needsLight |= step.Action == SceneAction.Flicker;
                needsPlayer |= step.Audience == SceneAudience.SelectedPlayer && step.Action is SceneAction.Sound or SceneAction.Scare or SceneAction.Caption or SceneAction.Hallucination or SceneAction.SignalCollapse;
                if (step.Action is SceneAction.Sound or SceneAction.Scare or SceneAction.Caption or SceneAction.Hallucination or SceneAction.SignalCollapse) { DirectorEvent.Validate(Event(Guid.NewGuid(), step, bindings)); }
            }
            // Pin native identities. An entity ID recycled after deletion must never retarget an armed scene.
            doorItem = Entity.FindEntityByID(bindings.Door) as Item; door = doorItem?.GetComponent<Door>();
            lightItem = Entity.FindEntityByID(bindings.Light) as Item;
            submarine = Entity.FindEntityByID(bindings.Submarine) as Submarine;
            if (needsDoor && door == null || needsLight && !ValidLight() || bindings.Submarine != 0 && submarine == null) { throw new ArgumentException("Выберите дверь, источник света и точку сцены"); }
        }
        private bool ValidLight() => lightItem is { Removed: false } && bindings.LightComponent < lightItem.Components.Count && lightItem.Components[bindings.LightComponent] is LightComponent;
        public SceneObservation Observe() {
            if (disposed || GameMain.GameSession?.IsRunning != true || !world.allows(owner, InteractionAction.DirectEvents) ||
                needsDoor && doorItem.Removed || changesDoor && !world.allows(owner, InteractionAction.ManageEntities) ||
                needsLight && (!ValidLight() || !world.allows(owner, InteractionAction.FlickerLight)) ||
                bindings.Submarine != 0 && submarine is not { Removed: false }) { return default; }
            Character character = needsPlayer ? world.target(bindings.Target) : null;
            if (needsPlayer && character is not { Removed: false, IsDead: false }) { return default; }
            Vector2 anchor = new Vector2(bindings.X, bindings.Y) + (submarine?.WorldPosition ?? Vector2.Zero);
            bool condition = program.Trigger switch {
                SceneTrigger.DoorOpened => door.IsOpen,
                SceneTrigger.EnterArea => Vector2.DistanceSquared(character.WorldPosition, anchor) <= bindings.Radius * bindings.Radius,
                _ => false
            };
            return new SceneObservation(true, condition);
        }
        public void Execute(in SceneEmission emission) {
            if (!Observe().Valid) { throw new InvalidOperationException("Привязки сцены больше недоступны"); }
            switch (emission.Step.Action) {
                case SceneAction.Flicker: world.light(emission.Id, lightItem, bindings.LightComponent, emission.Step.Duration); break;
                case SceneAction.OpenDoor:
                case SceneAction.CloseDoor:
                    bool open = emission.Step.Action == SceneAction.OpenDoor;
                    door.TrySetState(open, true, true);
                    if (door.IsOpen != open) { throw new InvalidOperationException("Дверь отклонила действие сцены"); }
                    break;
                default: world.publish(Event(emission.Id, emission.Step, bindings), owner); break;
            }
        }
        public void Cancel(in SceneEmission emission) {
            if (emission.Step.Action == SceneAction.Flicker) { world.light(emission.Id, lightItem, bindings.LightComponent, 0); }
            else if (emission.Step.Action is SceneAction.Sound or SceneAction.Scare or SceneAction.Caption or SceneAction.Hallucination or SceneAction.SignalCollapse) {
                world.publish(new DirectorEvent { Id = Guid.NewGuid().ToString("N"), Kind = DirectorKind.StopOne, Target = emission.Step.Audience == SceneAudience.Everyone ? "" : bindings.Target, Resource = emission.Id.ToString("N") }, owner);
            }
        }
        public void Dispose() => disposed = true;
    }
    internal static DirectorEvent Event(Guid id, SceneStep step, in SceneBindings bindings) => new() {
        Id = id.ToString("N"), Kind = step.Action switch { SceneAction.Sound => DirectorKind.Sound, SceneAction.Scare => DirectorKind.Scare, SceneAction.Hallucination => DirectorKind.Hallucination, SceneAction.SignalCollapse => DirectorKind.SignalCollapse, _ => DirectorKind.Caption },
        Target = step.Audience == SceneAudience.Everyone ? "" : bindings.Target, Resource = step.Resource, Visual = step.Visual, Text = step.Text, Gain = step.Gain,
        Duration = step.Duration, Interval = step.Interval, Intensity = step.Intensity, Behavior = step.Behavior, Scale = step.Scale, Speed = step.Speed, NearDistance = step.NearDistance,
        X = bindings.X, Y = bindings.Y, Submarine = bindings.Submarine, Radius = bindings.Radius
    };
}
