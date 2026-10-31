using Barotrauma;

namespace IEngine.StatusEffects;

static class StatusTagReference
{
    public static bool Matches(PropertyConditional conditional, ISerializableEntity target)
    {
        bool notEquals = conditional.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals;
        if (target == null) { return notEquals; }
        int found = 0;
        foreach (Identifier tag in conditional.AttributeValueAsTags)
        {
            bool tagFound = false;
            foreach (DurationListElement d in StatusEffect.DurationList)
            {
                if (d.Targets.Contains(target) && d.Parent.HasTag(tag)) { tagFound = true; break; }
            }
            if (!tagFound)
            {
                foreach (DelayedListElement d in DelayedEffect.DelayList)
                {
                    if (d.Targets.Contains(target) && d.Parent.HasTag(tag)) { tagFound = true; break; }
                }
            }
            if (tagFound) { found++; }
        }
        return notEquals ? found < conditional.AttributeValueAsTags.Length : found >= conditional.AttributeValueAsTags.Length;
    }
}
