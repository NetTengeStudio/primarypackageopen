using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

public static class SubRegistry
{
    public static int EditorIdLimit => Entity.ReservedIDStart;
    public const int MaxSpawned = SubListPayload.MaxSubs;
    public const ushort WorldId = ushort.MaxValue;

    private static readonly List<SubSpawn> spawned = new();
    [ThreadStatic] private static Submarine preserveOrigin;

    public static void Attach(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(Submarine), nameof(Submarine.RepositionEntities)),
        prefix: ModuleHost.Bind(typeof(SubRegistry), nameof(Repositioning)));

    private static bool Repositioning(IEnumerable<MapEntity> entities)
    {
        if (preserveOrigin == null) { return true; }
        foreach (MapEntity entity in entities) { if (entity.Submarine == preserveOrigin) { return false; } }
        return true;
    }
#if SERVER
    private static readonly List<SubmarineInfo> snapshots = new();
#endif

    public static IReadOnlyList<SubSpawn> Spawned => spawned;
    public static event Action<SubSpawn, string, bool> LoadFailed;

    public static void Reset()
    {
        spawned.Clear();
#if SERVER
        foreach (SubmarineInfo snapshot in snapshots)
        {
            string path = snapshot.FilePath;
            snapshot.Dispose();
            try { System.IO.File.Delete(path); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { ModLog.Once($"subeditor: snapshot cleanup failed: {error.Message}"); }
        }
        snapshots.Clear();
#endif
    }

#if SERVER
    public static bool Capture(Submarine submarine, in SubSpawn original, out SubSpawn snapshot)
    {
        snapshot = default;
        string path = System.IO.Path.Combine(LiveSave.ServerFolder, "_undo_" + Guid.NewGuid().ToString("N") + ".sub");
        try
        {
            System.IO.Directory.CreateDirectory(LiveSave.ServerFolder);
            SubmarineInfo info = LiveSave.Capture(submarine);
            info.FilePath = path;
            info.SaveAs(path);
            info.Dispose();
            info = new SubmarineInfo(path);
            string hash = info.MD5Hash.StringRepresentation;
            SubmarineInfo.AddToSavedSubs(info);
            snapshots.Add(info);
            Vector2 at = submarine.WorldPosition;
            snapshot = new SubSpawn(info.Name, hash, submarine.ID, original.IdOffset, at.X, at.Y, original.Thalamus, Exact: true);
            return true;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ModLog.Error($"subeditor: submarine snapshot failed: {error.Message}");
            return false;
        }
    }
#endif

    public static Submarine Find(ushort wireId) => wireId == 0 ? Submarine.MainSub : Submarine.Loaded.FirstOrDefault(s => s.ID == wireId);
    public static ushort WireId(Submarine sub) => sub == null ? WorldId : sub == Submarine.MainSub ? (ushort)0 : sub.ID;

    public static SubmarineInfo FindInfo(string name, string hash) =>
        SubmarineInfo.SavedSubmarines.FirstOrDefault(i => i.Name == name && (hash.Length == 0 || i.MD5Hash?.StringRepresentation == hash));

    private static Submarine Build(SubmarineInfo info, int idOffset, float x, float y, bool thalamus, ushort submarineId = 0, bool exact = false)
    {
        using var content = SubContent.Read(info);
        Submarine sub = null;
        using var world = WorldContext.Enter();
        bool worldEnabled = GameMain.World.Enabled;
        Submarine previousOrigin = preserveOrigin;
        using var reservation = new SubIds.Reservation(submarineId);
        try
        {
            XElement blueprint = EntityOps.Blueprint(info.SubmarineElement);
            int[] sourceIds = blueprint == null ? null : EntityOps.ElementIds(blueprint);
            if (sourceIds == null || !EntityIdMap.TryCreate(sourceIds, exact ? 0 : idOffset, out EntityIdMap ids)) { return null; }
            for (int i = 0; i < ids.Count; i++) { if (Entity.FindEntityByID(ids.Destination(i)) != null) { return null; } }
            sub = new Submarine(info, showErrorMessages: false, loadEntities: created =>
            {
                sub = created;
                if (exact) { preserveOrigin = created; }
                created.IdOffset = (ushort)idOffset;
                List<MapEntity> loaded = ExactIds.Load(created, blueprint, ids, exact ? 0 : idOffset);
                if (loaded.Count != sourceIds.Length) { throw new InvalidOperationException("Submarine entities were only partially loaded"); }
                return loaded;
            });
            if (sub.SubBody?.Body == null) { throw new InvalidOperationException("Submarine body was not created"); }
            sub.SetPosition(new Vector2(x, y));
            sub.SetPrevTransform(sub.Position);
            sub.UpdateTransform(interpolate: false);
            if (info.Type == SubmarineType.Wreck)
            {
                sub.MakeWreck();
                if (thalamus) { sub.CreateWreckAI(); } else { sub.DisableWreckAI(); }
            }
            return sub;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ModLog.Once($"subeditor: submarine \"{info.Name}\" could not be loaded: {e.Message}");
            if (sub != null) { Discard(sub); }
            return null;
        }
        finally { preserveOrigin = previousOrigin; GameMain.World.Enabled = worldEnabled; }
    }

    public static Submarine Load(in SubSpawn s)
    {
        ushort wireId = s.SubmarineId;
        if (spawned.Any(x => x.SubmarineId == wireId)) { return Find(wireId); }
        SubmarineInfo info = FindInfo(s.Name, s.Hash);
        if (info == null)
        {
            ModLog.Once($"subeditor: submarine \"{s.Name}\" ({s.Hash}) is not available on this peer");
            LoadFailed?.Invoke(s, "the file is missing or differs from the server's", true);
            return null;
        }
        if (Entity.FindEntityByID(s.SubmarineId) != null)
        {
            LoadFailed?.Invoke(s, "its id is occupied", false);
            return null;
        }
        Submarine sub = Build(info, s.IdOffset, s.X, s.Y, s.Thalamus, s.SubmarineId, s.Exact);
        if (sub == null) { LoadFailed?.Invoke(s, "it could not be built", false); return null; }
        if (sub.ID != s.SubmarineId)
        {
            ModLog.Once($"subeditor: submarine \"{s.Name}\" got id {sub.ID}, the server has {s.SubmarineId}");
            Discard(sub);
            LoadFailed?.Invoke(s, "its id differs from the server's", false);
            return null;
        }
        spawned.Add(s);
        return sub;
    }

    public static Submarine Spawn(SubmarineInfo info, int idOffset, Vector2 position, bool thalamus, out SubSpawn spawn)
    {
        spawn = default;
        string hash = info.MD5Hash?.StringRepresentation ?? "";
        Submarine sub = Build(info, idOffset, position.X, position.Y, thalamus);
        if (sub == null) { return null; }
        spawn = new SubSpawn(info.Name, hash, sub.ID, idOffset, position.X, position.Y, thalamus);
        spawned.Add(spawn);
        return sub;
    }

    public static bool Unload(ushort wireId)
    {
        int index = spawned.FindIndex(x => x.SubmarineId == wireId);
        if (index < 0) { return false; }
        spawned.RemoveAt(index);
        if (Submarine.Loaded.FirstOrDefault(x => x.ID == wireId) is Submarine sub) { Discard(sub); }
        return true;
    }

    public static bool CanLoad(in SubSpawn s)
    {
        SubmarineInfo info = FindInfo(s.Name, s.Hash);
        using var content = SubContent.Read(info);
        XElement blueprint = EntityOps.Blueprint(info?.SubmarineElement);
        int[] fileIds = blueprint == null ? null : EntityOps.ElementIds(blueprint);
        if (fileIds == null || !EntityIdMap.TryCreate(fileIds, s.Exact ? 0 : s.IdOffset, out EntityIdMap ids)) { return false; }
        if (Entity.FindEntityByID(s.SubmarineId) != null) { return false; }
        for (int k = 0; k < ids.Count; k++)
        {
            if (Entity.FindEntityByID(ids.Destination(k)) != null) { return false; }
        }
        return true;
    }

    public static void Reconcile(IReadOnlyList<SubSpawn> wanted)
    {
        foreach (SubSpawn have in spawned.ToArray())
        {
            if (!wanted.Contains(have)) { Unload(have.SubmarineId); }
        }
        foreach (SubSpawn s in wanted) { Load(s); }
    }

    private static void Discard(Submarine sub)
    {
        var aboard = new List<Character>();
        for (int i = 0; i < Character.CharacterList.Count; i++)
        {
            Character c = Character.CharacterList[i];
            if (c != null && !c.Removed && (c.Submarine == sub || c.CurrentHull?.Submarine == sub)) { aboard.Add(c); }
        }
        var doomed = new List<MapEntity>();
        for (int i = 0; i < MapEntity.MapEntityList.Count; i++)
        {
            MapEntity e = MapEntity.MapEntityList[i];
            if (e != null && !e.Removed && e.Submarine == sub) { doomed.Add(e); }
        }
        for (int i = 0; i < doomed.Count; i++) { doomed[i].Remove(); }
        Vector2[] where = new Vector2[aboard.Count];
        for (int i = 0; i < aboard.Count; i++) { where[i] = aboard[i].WorldPosition; }
        sub.Remove();
        for (int i = 0; i < aboard.Count; i++)
        {
            try { aboard[i].TeleportTo(where[i]); }
            catch (Exception e) when (e is NullReferenceException or InvalidOperationException) { ModLog.Once($"subeditor: a character could not leave the removed submarine: {e.Message}"); }
        }
    }
}
