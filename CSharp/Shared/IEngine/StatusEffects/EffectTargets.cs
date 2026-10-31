using System.Collections.Generic;
using Barotrauma;
using IEngine.Core;

namespace IEngine.StatusEffects;

static class EffectTargets
{
    public static void Filter(IReadOnlyList<ISerializableEntity> source, StatusEffect effect, List<ISerializableEntity> destination, bool specialize, bool batchValidation)
    {
        if (batchValidation && effect.TargetIdentifiers == null && !effect.OnlyInside && !effect.OnlyOutside && string.IsNullOrEmpty(effect.TargetItemComponent))
        {
            ListAppend.Copy(source, destination, specialize);
            return;
        }
        if (specialize && source is List<ISerializableEntity> list && list.GetType() == typeof(List<ISerializableEntity>) && list.Count != 0)
        {
            foreach (ISerializableEntity target in list)
            {
                if (effect.IsValidTarget(target)) { destination.Add(target); }
            }
            return;
        }
        foreach (ISerializableEntity target in source)
        {
            if (effect.IsValidTarget(target)) { destination.Add(target); }
        }
    }
}
