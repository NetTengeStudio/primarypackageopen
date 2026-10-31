using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;

namespace IEngine.Diagnostics;

static class Census
{
    public static string Brief()
    {
        int active = 0;
        foreach (Item item in Item.ItemList) { if (item.IsActive) { active++; } }
        return string.Create(CultureInfo.InvariantCulture,
            $"items={Item.ItemList.Count} active={active} hulls={Hull.HullList.Count} gaps={Gap.GapList.Count} chars={Character.CharacterList.Count} bodies={PhysicsBody.List.Count} powered={Powered.poweredList.Count} grids={Powered.Grids.Count} durations={StatusEffect.DurationList.Count} delays={DelayedEffect.DelayList.Count}");
    }

    public static string Full()
    {
        var byComponent = new Dictionary<string, (int Updating, int Total)>();
        var byPrefab = new Dictionary<string, int>();
        int active = 0, always = 0, bodies = 0, hidden = 0;
        foreach (Item item in Item.ItemList)
        {
            if (!item.IsActive) { continue; }
            active++;
            if (item.IsLayerHidden) { hidden++; }
            if (item.body is { Enabled: true }) { bodies++; }
            if (item.hasStatusEffectsOfType[(int)ActionType.Always]) { always++; }
            byPrefab[item.Prefab.Identifier.Value] = byPrefab.GetValueOrDefault(item.Prefab.Identifier.Value) + 1;
            foreach (ItemComponent ic in item.updateableComponents)
            {
                string key = ic.GetType().Name;
                var v = byComponent.GetValueOrDefault(key);
                byComponent[key] = (v.Updating + (ic.IsActive || ic.UpdateWhenInactive ? 1 : 0), v.Total + 1);
            }
        }
        var sb = new StringBuilder(Brief()).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"active items {active}, with Always effects {always}, enabled bodies {bodies}, layer hidden {hidden}\n");
        sb.Append("updateable components on active items (updating/total):\n");
        foreach (var kv in byComponent.OrderByDescending(k => k.Value.Updating).Take(30))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {kv.Key,-28} {kv.Value.Updating,6}/{kv.Value.Total}\n");
        }
        var contained = new Dictionary<string, int>();
        int aci = 0;
        foreach (Item item in Item.ItemList)
        {
            if (!item.IsActive) { continue; }
            foreach (ItemComponent ic in item.updateableComponents)
            {
                if (ic is not ItemContainer container || !(ic.IsActive || ic.UpdateWhenInactive)) { continue; }
                foreach (var a in container.activeContainedItems)
                {
                    aci++;
                    StatusEffect se = a.StatusEffect;
                    string key = $"{se.type} interval={(se.Interval > 0 ? "yes" : "no")} conditions={se.propertyConditionals?.Count ?? 0} targets={se.targetTypes}";
                    contained[key] = contained.GetValueOrDefault(key) + 1;
                }
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $"active contained-item effects on updating containers: {aci}\n");
        foreach (var kv in contained.OrderByDescending(k => k.Value).Take(20))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {kv.Value,6}  {kv.Key}\n");
        }
        var durations = new Dictionary<string, int>();
        foreach (DurationListElement d in StatusEffect.DurationList)
        {
            string owner = d.Entity switch { Item i => i.Prefab.Identifier.Value, Character c => "char:" + c.SpeciesName.Value, null => "null", _ => d.Entity.GetType().Name };
            string key = $"{owner} {d.Parent.type} duration={d.Duration:0.##} stackable={d.Parent.Stackable} always={d.Parent.CheckConditionalAlways} targets={d.Targets.Count}";
            durations[key] = durations.GetValueOrDefault(key) + 1;
        }
        foreach (DelayedListElement d in DelayedEffect.DelayList)
        {
            string owner = d.Entity switch { Item i => i.Prefab.Identifier.Value, Character c => "char:" + c.SpeciesName.Value, null => "null", _ => d.Entity.GetType().Name };
            string key = $"delayed {owner} {d.Parent.type} {d.Parent.delayType} delay={d.Parent.delay:0.##} left={d.Delay:0.#} stackable={d.Parent.Stackable} targets={d.Targets.Count}";
            durations[key] = durations.GetValueOrDefault(key) + 1;
        }
        sb.Append(CultureInfo.InvariantCulture, $"duration list: {StatusEffect.DurationList.Count}, delay list: {DelayedEffect.DelayList.Count}\n");
        foreach (var kv in durations.OrderByDescending(k => k.Value).Take(15))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {kv.Value,6}  {kv.Key}\n");
        }
        sb.Append("top active prefabs:\n");
        foreach (var kv in byPrefab.OrderByDescending(k => k.Value).Take(25))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {kv.Key,-36} {kv.Value}\n");
        }
        return sb.ToString();
    }
}
