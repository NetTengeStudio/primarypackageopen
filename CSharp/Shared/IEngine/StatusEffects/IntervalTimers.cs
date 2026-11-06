using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Barotrauma;

namespace IEngine.StatusEffects;

static class IntervalTimers
{
    public static bool ShouldWait(StatusEffect effect, Entity entity, float deltaTime)
    {
        {
            ref float timer = ref CollectionsMarshal.GetValueRefOrNullRef(effect.intervalTimers, entity);
            if (!Unsafe.IsNullRef(ref timer))
            {
                timer -= deltaTime;
                if (timer > 0f) { return true; }
            }
        }
        List<Entity> expired = StatusEffect.intervalsToRemove;
        expired.Clear();
        foreach (Entity candidate in effect.intervalTimers.Keys)
        {
            if (candidate.Removed) { expired.Add(candidate); }
        }
        foreach (Entity candidate in expired) { effect.intervalTimers.Remove(candidate); }
        return false;
    }
}
