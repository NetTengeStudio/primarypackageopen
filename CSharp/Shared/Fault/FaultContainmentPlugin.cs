using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Steam;
using PrimaryPackage.Networking;
using HarmonyLib;
using PrimaryPackage.Core;
using PrimaryPackage.Core.Fault;
using PrimaryPackage.Netcode;
using Barotrauma.Networking;
#if CLIENT
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace PrimaryPackage.Fault;

public sealed class FaultContainmentPlugin : IModule {
    private readonly PatchSet patches = new PatchSet();

    public void RoundStart() => FaultHooks.ResetEntities();

    public void Attach(Harmony harmony) {
        HarmonyMethod globalPrefix = ModuleHost.Bind(typeof(FaultHooks), nameof(FaultHooks.GlobalPrefix));
        HarmonyMethod globalFinalizer = ModuleHost.Bind(typeof(FaultHooks), nameof(FaultHooks.GlobalFinalizer));
        HarmonyMethod entityPrefix = ModuleHost.Bind(typeof(FaultHooks), nameof(FaultHooks.EntityPrefix));
        HarmonyMethod entityFinalizer = ModuleHost.Bind(typeof(FaultHooks), nameof(FaultHooks.EntityFinalizer));

        int armed = 0;
        int missing = 0;
        foreach (Boundary boundary in Boundaries()) {
            try {
                if (boundary.Method == null) { throw new MissingMethodException(boundary.Id); }
                FaultSite site = FaultHooks.Ledger.Register(boundary.Id, boundary.Policy, boundary.PerEntity);
                FaultHooks.Bind(boundary.Method, site);
                patches.Patch(harmony, boundary.Method,
                    prefix: boundary.Prefix ?? (boundary.PerEntity ? entityPrefix : globalPrefix),
                    finalizer: boundary.Finalizer ?? (boundary.PerEntity ? entityFinalizer : globalFinalizer));
                if (boundary.Method.DeclaringType == typeof(Item) && boundary.Method.Name == nameof(Item.Update)) {
                    FaultHooks.ObserveItemUpdates(site, entityPrefix.method, entityFinalizer.method);
                }
                armed++;
            } catch (Exception error) {
                missing++;
                ModLog.Error($"fault boundary {boundary.Id} is not armed: {error.Message}");
            }
        }
        ModLog.Message($"fault containment armed on {armed} boundaries" + (missing > 0 ? $", {missing} missing" : ""));

        ModCommands.Register("faults", "faults: lists the faults the mod contained instead of crashing and what is paused",
            _ => Print(), localOnClient: true, relayToServer: false);
        ModCommands.Register("faults_reset", "faults_reset: forgets paused entities so they are tried again immediately",
            _ => {
                FaultHooks.ResetEntities();
                DebugConsole.NewMessage("fault records of entities cleared", Microsoft.Xna.Framework.Color.LightGreen);
            }, localOnClient: true, relayToServer: false);
    }

    public void Release() {
        patches.Revert(ModuleHost.Instance);
        FaultHooks.Release();
    }

    private static void Print() {
        List<string> lines = new List<string>();
        FaultHooks.Ledger.Describe(Timing.TotalTime, lines);
        for (int i = 0; i < lines.Count; i++) {
            DebugConsole.NewMessage(lines[i], Microsoft.Xna.Framework.Color.Cyan);
        }
    }

    private sealed class Boundary {
        public string Id;
        public MethodBase Method;
        public FaultPolicy Policy;
        public bool PerEntity;
        public HarmonyMethod Finalizer;
        public HarmonyMethod Prefix = null;
    }

    private static Boundary Tick(string id, MethodBase method) =>
        new Boundary { Id = id, Method = method, Policy = FaultPolicy.Tick };

    private static Boundary Essential(string id, MethodBase method) =>
        new Boundary { Id = id, Method = method, Policy = FaultPolicy.Essential };

    private static Boundary Entity(string id, MethodBase method) =>
        new Boundary { Id = id, Method = method, Policy = FaultPolicy.Entity, PerEntity = true };

#if CLIENT
    private static Boundary Draw(string id, MethodBase method, bool guardLoss = false, bool presentLoss = false) =>
        new Boundary { Id = id, Method = method, Policy = FaultPolicy.Essential,
            Prefix = guardLoss ? ModuleHost.Bind(typeof(DeviceLossGuard), nameof(DeviceLossGuard.DrawPrefix)) : null,
            Finalizer = ModuleHost.Bind(presentLoss ? typeof(DeviceLossGuard) : typeof(RenderRepair),
                presentLoss ? nameof(DeviceLossGuard.PresentFinalizer) : nameof(RenderRepair.DrawFinalizer)) };
#endif

    private static Boundary Thread(string id, MethodBase method) =>
        new Boundary { Id = id, Method = method, Policy = FaultPolicy.Essential,
            Finalizer = ModuleHost.Bind(typeof(ChildServerRelayPatch), nameof(ChildServerRelayPatch.ThreadFinalizer)) };

    private static IEnumerable<Boundary> Boundaries() {
        Type[] timed = { typeof(float) };
        Type[] timedDouble = { typeof(double) };
        Type[] withCamera = { typeof(float), typeof(Camera) };

        yield return Tick("RespawnManager.Update", AccessTools.Method(typeof(RespawnManager), "Update", timed));
        yield return Tick("EventManager.Update", AccessTools.Method(typeof(EventManager), "Update", timed));
        yield return Tick("GameSession.Update", AccessTools.Method(typeof(GameSession), "Update", timed));
        yield return Tick("CrewManager.Update", AccessTools.Method(typeof(CrewManager), "Update", timed));
        yield return Tick("Level.Update", AccessTools.Method(typeof(Level), "Update", withCamera));
        yield return Tick("TaskPool.Update", AccessTools.Method(typeof(TaskPool), "Update", Type.EmptyTypes));
        yield return Tick("Powered.UpdatePower", AccessTools.Method(typeof(Powered), "UpdatePower", timed));

        yield return Essential("GameScreen.Update", AccessTools.Method(typeof(GameScreen), "Update", timedDouble));
        yield return Essential("CoroutineManager.Update", AccessTools.Method(typeof(CoroutineManager), "Update", new[] { typeof(bool), typeof(float) }));
        yield return Essential("SteamManager.Update", AccessTools.Method(typeof(SteamManager), "Update", timed));
        yield return Thread("ChildServerRelay.UpdateRead", AccessTools.Method(typeof(ChildServerRelay), "UpdateRead", Type.EmptyTypes));
        yield return Thread("ChildServerRelay.UpdateWrite", AccessTools.Method(typeof(ChildServerRelay), "UpdateWrite", Type.EmptyTypes));
        yield return Essential("StatusEffect.UpdateAll", AccessTools.Method(typeof(StatusEffect), "UpdateAll", timed));
        yield return Essential("Ragdoll.UpdateAll", AccessTools.Method(typeof(Ragdoll), "UpdateAll", withCamera));

        yield return Entity("Item.Update", AccessTools.Method(typeof(Item), "Update", withCamera));
        yield return Entity("Character.Update", AccessTools.Method(typeof(Character), "Update", withCamera));
        yield return Entity("Hull.Update", AccessTools.Method(typeof(Hull), "Update", withCamera));
        yield return Entity("Gap.Update", AccessTools.Method(typeof(Gap), "Update", withCamera));
        yield return Entity("Structure.Update", AccessTools.Method(typeof(Structure), "Update", withCamera));
        yield return Entity("Submarine.Update", AccessTools.Method(typeof(Submarine), "Update", timed));
#if SERVER
        yield return Essential("DebugConsole.Update", AccessTools.Method(typeof(DebugConsole), "Update", Type.EmptyTypes));
        yield return Essential("GameServer.Update", AccessTools.Method(typeof(GameServer), "Update", timed));
#endif
#if CLIENT
        yield return Essential("DebugConsole.Update", AccessTools.Method(typeof(DebugConsole), "Update", timed));
        yield return Essential("GameClient.Update", AccessTools.Method(typeof(GameClient), "Update", timed));
        yield return Essential("GameMain.Update", AccessTools.Method(typeof(GameMain), "Update", new[] { typeof(GameTime) }));
        yield return Essential("GUI.Update", AccessTools.Method(typeof(GUI), "Update", timed));
        yield return Draw("GameScreen.Draw", AccessTools.Method(typeof(GameScreen), "Draw", new[] { typeof(double), typeof(GraphicsDevice), typeof(SpriteBatch) }));
        yield return Draw("GameMain.Draw", AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }), true);
        yield return Draw("GraphicsDevice.Present", AccessTools.Method(typeof(GraphicsDevice), "Present", Type.EmptyTypes), false, true);
#endif
    }
}
