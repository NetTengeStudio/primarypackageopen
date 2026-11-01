using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PrimaryPackage.Core;
using Barotrauma;
using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private sealed record ExportChange(ulong Sequence, Guid Operation, string Actor, Guid Incarnation, ushort Component,
        string Property, string Before, string After, long Timestamp, string Kind);
    private sealed record ExportTarget(Guid Incarnation, ushort NativeId, string Prefab, string Submarine);
    private sealed record EditExport(int Version, string Scope, DateTimeOffset CapturedUtc, ExportChange[] Changes, ExportTarget[] CurrentTargets);
    private readonly ConcurrentQueue<(Session Session, string File, string Error)> completedExports = new();
    private Task exportTask;
    private void ExportWorldEdits(Session session) {
        Require(session.Client, InteractionAction.ManageEntities);
        if (exportTask is { IsCompleted: false }) { throw new InvalidOperationException("Предыдущий экспорт ещё записывается"); }
        // Capture immutable records on the server thread. File IO and serialization never touch the engine.
        var changes = new ExportChange[worldEdits.Changes.Count];
        for (int i = 0; i < changes.Length; i++) {
            var entry = worldEdits.Changes[i]; changes[i] = new(entry.Sequence, entry.Request, entry.Actor, entry.Key.Incarnation,
                entry.Key.Component, entry.Key.Property, entry.Before, entry.After, entry.Timestamp, entry.Kind.ToString());
        }
        var targets = new List<ExportTarget>(devices.Count);
        foreach (var lease in devices.Values) {
            if (lease.Current) { targets.Add(new(lease.Id, lease.Item.ID, lease.Item.Prefab.Identifier.Value, lease.Item.Submarine?.Info?.Name ?? "")); }
        }
        var snapshot = new EditExport(1, "Round operation audit; not a submarine blueprint or a portable preset", DateTimeOffset.UtcNow, changes, targets.ToArray());
        string name = "world-edits-" + Guid.NewGuid().ToString("N") + ".json";
        string path = Path.Combine(ModAssets.Resolve("Data/Interactions/WorldExports"), name);
        exportTask = Task.Run(() => {
            try { new AtomicStore<EditExport>(path).Save(snapshot); completedExports.Enqueue((session, name, "")); }
            catch (Exception error) { completedExports.Enqueue((session, "", error.Message)); }
        });
    }
    private static void SendError(Client client, string text) => ModNet.Send(InteractionProtocol.Channel, client.Connection, output => {
        InteractionProtocol.Header(output, InteractionPacket.Error); output.WriteString(text.Length > 1600 ? text[..1600] : text);
    });
    private void ReportWorldExports() {
        while (completedExports.TryDequeue(out var result)) {
            if (!sessions.TryGetValue(result.Session.Client.SessionId, out var current) || current != result.Session) { continue; }
            if (result.Error.Length > 0) { SendError(current.Client, "Экспорт журнала не записан: " + result.Error); }
            else { GameMain.Server.SendConsoleMessage("Журнал мира сохранён: Data/Interactions/WorldExports/" + result.File, current.Client); }
        }
    }
}
