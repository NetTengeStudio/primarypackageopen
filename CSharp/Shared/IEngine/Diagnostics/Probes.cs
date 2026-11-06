using System;
using System.Diagnostics;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using IEngine.Core;

namespace IEngine.Diagnostics;

static partial class Probes
{
    public static readonly PhaseRecorder Recorder = new(1 << 16);
    public static event Action TickEnded;
    public static event Action SimTickEnded;

    static bool installed;

    public static void Ensure()
    {
        if (installed || EngineHost.Harmony == null) { return; }
        installed = true;
        Harmony harmony = EngineHost.Harmony;
        EngineCounters.Install(harmony);
        Wrap(harmony, AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update)), nameof(TickPost));
        Wrap(harmony, AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll)), nameof(MapEntityPost));
        Wrap(harmony, AccessTools.Method(typeof(EntitySpawner), nameof(EntitySpawner.Update)), nameof(SpawningPost));
        Wrap(harmony, AccessTools.Method(typeof(Powered), nameof(Powered.UpdatePower)), nameof(PowerPost));
        Wrap(harmony, AccessTools.Method(typeof(Character), nameof(Character.UpdateAll)), nameof(CharactersPost));
        Wrap(harmony, AccessTools.Method(typeof(Character), nameof(Character.UpdateAnimAll)), nameof(AnimationPost));
        Wrap(harmony, AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.UpdateAll)), nameof(StatusEffectsPost));
        Wrap(harmony, AccessTools.Method(typeof(Ragdoll), nameof(Ragdoll.UpdateAll)), nameof(RagdollsPost));
        Wrap(harmony, AccessTools.Method(typeof(World), nameof(World.Step), new[] { typeof(float) }), nameof(PhysicsPost));
        InstallSide(harmony);
    }

    static partial void InstallSide(Harmony harmony);

    public static void Uninstall() => installed = false;

    static void Wrap(Harmony harmony, System.Reflection.MethodBase target, string postfix)
    {
        if (target == null) { return; }
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(Probes), nameof(Pre)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(Probes), postfix) { priority = Priority.Last });
    }

    static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

#if CLIENT
    static bool perFrame;
#endif

    static void TickPost(long __state)
    {
        Recorder.Add(Phase.Tick, __state);
        Recorder.EndSimTick();
        SimTickEnded?.Invoke();
#if CLIENT
        if (!perFrame) { EndSample(); }
#else
        EndSample();
#endif
    }

    static void EndSample()
    {
        Recorder.EndTick();
        TickEnded?.Invoke();
    }

    static void MapEntityPost(long __state) => Recorder.Add(Phase.MapEntity, __state);
    static void SpawningPost(long __state) => Recorder.Add(Phase.Spawning, __state);
    static void PowerPost(long __state) => Recorder.Add(Phase.Power, __state);
    static void CharactersPost(long __state) => Recorder.Add(Phase.Characters, __state);
    static void AnimationPost(long __state) => Recorder.Add(Phase.Animation, __state);
    static void StatusEffectsPost(long __state) => Recorder.Add(Phase.StatusEffects, __state);
    static void RagdollsPost(long __state) => Recorder.Add(Phase.Ragdolls, __state);
    static void PhysicsPost(long __state) => Recorder.Add(Phase.Physics, __state);
    static void NetworkPost(long __state) => Recorder.Add(Phase.Network, __state);
    static void DrawPost(long __state) => Recorder.Add(Phase.Draw, __state);
}
