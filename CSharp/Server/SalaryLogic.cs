using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.CrewManifest {
    [HarmonyPatch(typeof(GameSession), nameof(GameSession.StartRound),
        new Type[] { typeof(LevelData), typeof(bool), typeof(SubmarineInfo), typeof(SubmarineInfo) })]
    internal static class SalaryRoundStartPatch {
        [HarmonyPostfix]
        public static void Postfix() => SalaryState.Clear();
    }
    [HarmonyPatch(typeof(Item), MethodType.Constructor,
        new Type[] { typeof(Rectangle), typeof(ItemPrefab), typeof(Submarine), typeof(bool), typeof(ushort) })]
    internal static class SalaryEnvelopeSpawnPatch {
        [HarmonyPostfix]
        public static void Postfix(Item __instance) {
            if (__instance?.Prefab == null || __instance.Prefab.Identifier != "erc_salaryenvelope") { return; }
            if (SalaryState.PaidThisRound) { return; }
            SalaryState.PaidThisRound = true;
            Item item = __instance;
            CoroutineManager.Invoke(() => {
                if (item == null || item.Removed) {
                    SalaryState.PaidThisRound = false;
                    return;
                }
                int count = SalaryLogic.FillEnvelope(item);
                if (count <= 0) {
                    SalaryState.PaidThisRound = false;
                }
            }, 0.3f);
        }
    }
    internal static class SalaryLogic {
        private static bool IsLivingCrew(Character character) =>
            character != null &&
            character.IsHuman &&
            !character.IsDead &&
            character.TeamID == CharacterTeamType.Team1 &&
            character.Info != null;
        private static Identifier GetJobId(Character character) =>
            character?.Info?.Job?.Prefab?.Identifier ?? default;
        private static List<Identifier> CollectActiveJobs() {
            var seen = new HashSet<Identifier>();
            var jobs = new List<Identifier>();
            foreach (Character character in Character.CharacterList) {
                if (!IsLivingCrew(character)) { continue; }
                Identifier jobId = GetJobId(character);
                if (jobId.IsEmpty) { continue; }
                if (!SalaryData.Salary.TryGetValue(jobId, out int amount) || amount <= 0) { continue; }
                if (seen.Add(jobId)) { jobs.Add(jobId); }
            }
            return jobs;
        }
        private static void PayOut(int amount, ItemInventory inventory) {
            int remaining = amount;
            foreach (var bill in SalaryData.Bills) {
                int count = remaining / bill.Value;
                remaining %= bill.Value;
                if (count <= 0) { continue; }
                if (!ItemPrefab.Prefabs.TryGet(bill.Id, out ItemPrefab prefab)) { continue; }
                for (int i = 0; i < count; i++) {
                    Entity.Spawner.AddItemToSpawnQueue(prefab, inventory);
                }
            }
        }
        private static void SpawnSalaryEnvelopeInto(Identifier jobId, int amount, ItemInventory targetInventory) {
            if (targetInventory == null || !ItemPrefab.Prefabs.TryGet("erc_salaryenvelope_small", out ItemPrefab prefab)) { return; }
            Entity.Spawner.AddItemToSpawnQueue(prefab, targetInventory, onSpawned: env => {
                if (env == null) { return; }
                try { env.AddTag("salary_" + jobId.Value); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("SalaryLogic.1", noted); }
                string roleName = SalaryData.RoleName.TryGetValue(jobId, out string name) ? name : jobId.Value;
                try { env.Description = $"Зарплата за смену: {roleName} ({amount} кр.)"; } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("SalaryLogic.2", noted); }
                if (env.OwnInventory != null) { PayOut(amount, env.OwnInventory); }
                SalaryState.PaidJobs.Add(jobId);
            });
        }
        public static int FillEnvelope(Item bigItem) {
            if (bigItem?.OwnInventory == null) { return 0; }
            List<Identifier> jobs = CollectActiveJobs();
            int count = 0;
            foreach (Identifier jobId in jobs) {
                SpawnSalaryEnvelopeInto(jobId, SalaryData.Salary[jobId], bigItem.OwnInventory);
                count++;
            }
            return count;
        }
    }
}
