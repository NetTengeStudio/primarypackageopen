using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    internal sealed class SceneRow {
        internal Guid Id;
        internal uint Revision;
        internal string Name, Owner, Error;
        internal SceneTrigger Trigger;
        internal SceneState State;
    }
    internal readonly List<SceneRow> SceneRows = new(SceneRuntime.Capacity);
    internal event Action ScenesChanged;
    private readonly SceneRuntime localScenes = new();
    private SceneWorld sceneWorld;
    private double nextSceneTick;
    private ulong localSceneVersion;
    private void InitializeScenes() => sceneWorld = new SceneWorld(_ => Character.Controlled,
        (_, action) => !Multiplayer && Allows(action), (value, _) => playback.Start(value),
        (id, item, component, duration) => {
            if (duration == 0) { LightEffectService.Instance.Cancel(id); }
            else { LightEffectService.Instance.Start((LightComponent)item.Components[component], duration, id); }
        });
    private void DirectorFailed(string id, string error) {
        if (!Multiplayer) { if (Guid.TryParseExact(id, "N", out Guid effect)) { localScenes.FailEffect(effect, error); } views.Error(error); return; }
        Send(InteractionPacket.DirectorFailure, output => { output.WriteString(id); output.WriteString(error); });
    }
    internal void ArmScene(SceneProgram program, in SceneBindings bindings) {
        if (!Allows(InteractionAction.DirectEvents)) { throw new UnauthorizedAccessException("Нет разрешения управлять сценами"); }
        program.Validate();
        if (!Multiplayer) { localScenes.Arm(Guid.NewGuid(), 0, "local", program, bindings, GUIRuntime.Now, sceneWorld); RequestScenes(); return; }
        SceneBindings copy = bindings;
        Send(InteractionPacket.ScenePut, output => { output.WriteString(Guid.NewGuid().ToString("N")); output.WriteUInt32(0); SceneWire.Write(output, program, copy); });
    }
    internal void RemoveScene(SceneRow scene) {
        if (!Allows(InteractionAction.DirectEvents)) { return; }
        if (!Multiplayer) { localScenes.Remove(scene.Id, scene.Revision); RequestScenes(); return; }
        Send(InteractionPacket.SceneRemove, output => { output.WriteString(scene.Id.ToString("N")); output.WriteUInt32(scene.Revision); });
    }
    internal void RequestScenes() {
        if (Multiplayer) { Send(InteractionPacket.SceneList, null); return; }
        localSceneVersion = localScenes.Version;
        SceneRows.Clear();
        foreach (SceneRuntime.Entry entry in localScenes.Entries) { SceneRows.Add(new SceneRow { Id = entry.Id, Revision = entry.Revision, Name = entry.Name, Owner = entry.Owner, Trigger = entry.Trigger, State = entry.State, Error = entry.Error }); }
        ScenesChanged?.Invoke();
    }
    private void ReadScenes(IReadMessage input) {
        int count = input.ReadByte();
        if (count > SceneRuntime.Capacity) { throw new ArgumentException("Недопустимый список сцен"); }
        var received = new List<SceneRow>(count);
        for (int i = 0; i < count; i++) {
            var row = new SceneRow { Id = Guid.ParseExact(input.ReadString(), "N"), Revision = input.ReadUInt32(), Name = input.ReadString(), Owner = input.ReadString(), Trigger = (SceneTrigger)input.ReadByte(), State = (SceneState)input.ReadByte(), Error = input.ReadString() };
            if (!Enum.IsDefined(row.Trigger) || !Enum.IsDefined(row.State) || row.Name.Length > 64 || row.Error.Length > 1024) { throw new ArgumentException("Недопустимая сцена"); }
            received.Add(row);
        }
        SceneRows.Clear(); SceneRows.AddRange(received); ScenesChanged?.Invoke();
    }
}
