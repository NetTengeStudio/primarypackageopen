using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
namespace PrimaryPackage.CrewManifest {
    internal static class ManifestLogic {
        public const string CaptainJob = "captain";
        public static bool TryApply(Item consoleItem, Character requester, Identifier jobId, out string error) {
            error = null;
            if (!JobPrefab.Prefabs.TryGet(jobId, out var jobPrefab) || jobPrefab.HiddenJob) { error = "Недопустимая роль"; return false; }
            if (!HasCaptainAccess(requester, out error)) { return false; }
            if (!TryGetTargetFromInsertedCard(consoleItem, out var target, out _, out error)) { return false; }
            ApplyAuthoritative(target, jobId, target.Info.Job?.Prefab?.Identifier ?? default);
            return true;
        }
        public static bool HasCaptainAccess(Character requester, out string error) {
            error = null;
            if (requester?.Info == null) { error = "Нет запросителя"; return false; }
            var idCard = requester.Inventory?.GetItemInLimbSlot(InvSlotType.Card)?.GetComponent<IdCard>();
            if (idCard == null) { error = "Наденьте ID-карту"; return false; }
            if (idCard.OwnerJobId != CaptainJob) { error = "Карта не капитанская"; return false; }
            if (!string.Equals(idCard.OwnerName, requester.Info.Name, System.StringComparison.Ordinal)) {
                error = "Это не ваша ID-карта";
                return false;
            }
            return true;
        }
        public static bool TryGetTargetFromInsertedCard(Item consoleItem, out Character target, out IdCard idCard, out string error) {
            target = null;
            idCard = null;
            error = null;
            var container = consoleItem?.GetComponent<ItemContainer>();
            var cardItem = container?.Inventory?.AllItems?.FirstOrDefault(it => it.GetComponent<IdCard>() != null);
            if (cardItem == null) { error = "Вставьте ID-карту члена экипажа"; return false; }
            idCard = cardItem.GetComponent<IdCard>();
            string ownerName = idCard.OwnerName;
            target = Character.CharacterList.FirstOrDefault(c => c?.Info != null && !c.IsDead && c.IsHuman &&
                string.Equals(c.Info.Name, ownerName, System.StringComparison.Ordinal));
            if (target == null) { error = "Владелец карты не найден в экипаже"; return false; }
            return true;
        }
        public static void ApplyAuthoritative(Character target, Identifier newJobId, Identifier leavingRole) {
            if (target?.Info == null || !JobPrefab.Prefabs.TryGet(newJobId, out var jobPrefab)) { return; }
            var rec = ManifestState.GetOrCreate(target.Info);
            if (rec.OriginalJob.IsEmpty) { rec.OriginalJob = leavingRole.IsEmpty ? (target.Info.Job?.Prefab?.Identifier ?? default) : leavingRole; }
            SetActiveJob(target, jobPrefab, rec, newJobId);
        }
        private static void SetActiveJob(Character target, JobPrefab jobPrefab, RoleRecord rec, Identifier currentJobId) {
            target.Info.Job = new Job(jobPrefab, isPvP: false, Rand.RandSync.Unsynced, variant: 0);
            rec.CurrentJob = currentJobId;
            RefreshIdCardJob(target);
            ManifestState.OnJobChangedLocal?.Invoke(target);
        }
        public static void RefreshIdCardJob(Character target) {
            if (target?.Info == null) { return; }
            string ownerName = target.Info.Name;
            Identifier jobId = target.Info.Job?.Prefab?.Identifier ?? default;
            string jobName = target.Info.Job?.Prefab?.Name.Value ?? "-";
            foreach (Item it in Item.ItemList) {
                var card = it.GetComponent<IdCard>();
                if (card == null || !string.Equals(card.OwnerName, ownerName, System.StringComparison.Ordinal)) { continue; }
                Identifier oldJobId = card.OwnerJobId;
                card.OwnerJobId = jobId;
                if (!oldJobId.IsEmpty) { it.RemoveTag($"jobid:{oldJobId}".ToIdentifier()); }
                if (!jobId.IsEmpty) { it.AddTag($"jobid:{jobId}"); }
                string updated = IdCardDescriptionPatch.ReplaceProfessionLine(card.Description, jobName);
                card.Description = updated;
                it.Description = updated;
            }
        }
        public static bool TryGetOriginalJobPrefab(CharacterInfo info, out JobPrefab originalPrefab) {
            originalPrefab = null;
            return ManifestState.TryGetComposite(info, out var originalJob, out _)
                && JobPrefab.Prefabs.TryGet(originalJob, out originalPrefab);
        }
        public static void ApplyFromSync(Character target, Identifier originalJobId, Identifier currentJobId) {
            if (target?.Info == null || !JobPrefab.Prefabs.TryGet(currentJobId, out var jobPrefab)) { return; }
            var rec = ManifestState.GetOrCreate(target.Info);
            rec.OriginalJob = originalJobId;
            SetActiveJob(target, jobPrefab, rec, currentJobId);
        }
    }
}
