using System;
using System.Runtime.CompilerServices;
using Barotrauma;
namespace PrimaryPackage.CrewManifest {
    internal sealed class RoleRecord {
        public Identifier OriginalJob;
        public Identifier CurrentJob;
    }
    internal static class ManifestState {
        private static readonly ConditionalWeakTable<CharacterInfo, RoleRecord> records = new();
        public static Action<Character> OnJobChangedLocal;
        public static bool ClientFullSyncRequested;
        public static bool TryGet(CharacterInfo info, out RoleRecord rec) {
            if (info == null) { rec = null; return false; }
            return records.TryGetValue(info, out rec);
        }
        public static void Clear() {
            records.Clear();
            ClientFullSyncRequested = false;
        }
        public static RoleRecord GetOrCreate(CharacterInfo info) => info == null ? null : records.GetOrCreateValue(info);
        public static bool TryGetComposite(CharacterInfo info, out Identifier original, out Identifier current) {
            original = default; current = default;
            if (info == null || !records.TryGetValue(info, out var rec)) { return false; }
            if (rec.OriginalJob.IsEmpty || rec.OriginalJob == rec.CurrentJob) { return false; }
            original = rec.OriginalJob; current = rec.CurrentJob;
            return true;
        }
    }
}
