using HarmonyLib;
using Barotrauma;
using Barotrauma.Items.Components;
namespace PrimaryPackage.CrewManifest {
    [HarmonyPatch(typeof(IdCard), nameof(IdCard.Initialize))]
    internal static class IdCardDescriptionPatch {
        private const string OwnerLabelColor = "#c5e8f3";
        private const string WorkplaceLabelColor = "#96a0c1";
        private const string JobLabelColor = "#8a8a8a";
        private const string IdDocLabelColor = "#c5b06e";
        private const string ProfessionLabel = "Профессия";
        [HarmonyPostfix]
        public static void Postfix(IdCard __instance, WayPoint spawnPoint, Character character) {
            if (spawnPoint == null || character?.Info == null) { return; }
            string workplace = spawnPoint.Submarine?.Info?.DisplayName.Value;
            if (string.IsNullOrWhiteSpace(workplace)) { return; }
            string jobName = character.Info.Job?.Prefab?.Name.Value ?? "-";
            string description =
                $"‖color:{OwnerLabelColor}‖ФИО‖color:end‖: {character.Info.Name}\n" +
                $"‖color:{WorkplaceLabelColor}‖Место работы‖color:end‖: {workplace}\n" +
                ProfessionLine(jobName) + "\n" +
                $"‖color:{IdDocLabelColor}‖ID документа‖color:end‖: {__instance.Item.ID}";
            __instance.Description = description;
            __instance.Item.Description = description;
        }
        public static string ProfessionLine(string jobName) =>
            $"‖color:{JobLabelColor}‖{ProfessionLabel}‖color:end‖: {jobName}";
        public static string ReplaceProfessionLine(string description, string jobName) {
            if (string.IsNullOrEmpty(description)) { return description; }
            string[] lines = description.Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                if (lines[i].Contains(ProfessionLabel)) { lines[i] = ProfessionLine(jobName); return string.Join("\n", lines); }
            }
            return description;
        }
    }
}
