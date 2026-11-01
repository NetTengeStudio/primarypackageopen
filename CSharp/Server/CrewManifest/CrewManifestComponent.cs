using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.CrewManifest {
    public partial class CrewManifestComponent {
        public void ServerEventRead(IReadMessage msg, Client c) {
            byte kind = msg.ReadByte();
            if (kind == 0) {
                Identifier jobId = ReadId(msg);
                if (!item.CanClientAccess(c)) { return; }
                if (ManifestLogic.TryApply(item, c.Character, jobId, out _) &&
                    ManifestLogic.TryGetTargetFromInsertedCard(item, out var target, out _, out _)) {
                    item.CreateServerEvent(this, new BroadcastEventData(target.ID));
                }
            }
            else { item.CreateServerEvent(this, new FullSyncEventData()); }
        }
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            if (TryExtractEventData<BroadcastEventData>(extraData, out var single)) {
                msg.WriteByte((byte)0);
                WriteCharacterState(msg, single.TargetId);
                return;
            }
            msg.WriteByte((byte)1);
            var changed = new List<Character>();
            foreach (Character ch in Character.CharacterList) {
                if (ch?.Info != null && ManifestState.TryGetComposite(ch.Info, out _, out _)) { changed.Add(ch); }
            }
            msg.WriteUInt16((ushort)changed.Count);
            foreach (Character ch in changed) { WriteCharacterState(msg, ch.ID); }
        }
        private static void WriteCharacterState(IWriteMessage msg, ushort targetId) {
            Identifier original = default, current = default;
            if (Entity.FindEntityByID(targetId) is Character target && target.Info != null) {
                if (ManifestState.TryGet(target.Info, out var rec)) { original = rec.OriginalJob; }
                current = target.Info.Job?.Prefab?.Identifier ?? default;
            }
            msg.WriteUInt16(targetId);
            WriteId(msg, original);
            WriteId(msg, current);
        }
    }
}
