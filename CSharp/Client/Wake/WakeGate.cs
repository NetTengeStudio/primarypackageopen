using Barotrauma;
using Barotrauma.Networking;

namespace PrimaryPackage.Wake;

public static class WakeGate {
    public static bool Privileged {
        get {
            if (GameMain.Client is not GameClient client) { return false; }
            return client.IsServerOwner
                || client.HasPermission(ClientPermissions.ConsoleCommands)
                || client.HasPermission(ClientPermissions.All);
        }
    }

    public static bool Skipped(Character character) {
        if (character == null) { return true; }
        Identifier job = character.Info?.Job?.Prefab?.Identifier ?? Identifier.Empty;
        if (!job.IsEmpty) {
            for (int i = 0; i < WakeConfig.SkipJobs.Length; i++) {
                if (WakeConfig.SkipJobs[i] == job) { return true; }
            }
        }
        CharacterHealth health = character.CharacterHealth;
        if (health == null) { return false; }
        for (int i = 0; i < WakeConfig.SkipAfflictions.Length; i++) {
            Affliction affliction = health.GetAffliction(WakeConfig.SkipAfflictions[i]);
            if (affliction != null && affliction.Strength > 0.0f) { return true; }
        }
        return false;
    }

    public static bool Applies(Character character) => Privileged || Skipped(character);
}
