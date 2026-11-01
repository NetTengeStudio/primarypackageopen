using Barotrauma;

namespace PrimaryPackage.Emotions;

internal static class EmotionFacts {
    internal static EmoteFacts Read(Character actor, bool busy, double sinceLast) {
        if (actor == null) { return default; }
        bool right = actor.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand) != null;
        bool left = actor.Inventory?.GetItemInLimbSlot(InvSlotType.LeftHand) != null;
        return new EmoteFacts {
            Alive = !actor.IsDead && !actor.Removed,
            Conscious = !actor.IsIncapacitated && actor.Stun <= 0,
            Bound = actor.LockHands, BothHandsBusy = right && left, AnyHandBusy = right || left,
            Muted = actor.SpeechImpediment >= 100, Emoting = busy,
            Vitality = actor.MaxVitality > 0 ? actor.Vitality / actor.MaxVitality : 1,
            SinceLast = sinceLast
        };
    }
    internal static string Gender(Character actor) {
        if (actor.Info?.Head?.Preset?.TagSet is not { } tags) { return null; }
        foreach (Identifier tag in tags) { if (tag == "female") { return "female"; } if (tag == "male") { return "male"; } }
        return null;
    }
}
