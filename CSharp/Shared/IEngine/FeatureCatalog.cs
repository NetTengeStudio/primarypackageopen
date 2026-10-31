using System.Collections.Generic;
using IEngine.Execution;
using IEngine.Power;
using IEngine.StatusEffects;

namespace IEngine;

static partial class FeatureCatalog
{
    public static List<Feature> Create()
    {
        var list = new List<Feature> { new ExecutorFeature(), new ActiveSetFeature(), new StatusTagFeature(), new PowerFeature(), new TagsFeature(), new ListRemovalFeature(), new Simulation.ContainerFeature(), new Simulation.SpatialFeature(), new BodiesFeature(), new LuaBusFeature(), new EffectCompilerFeature(), new SchedulerFeature(), new LuaEventsFeature(), new ComponentPlansFeature(), new ItemTargetsFeature(), new FireFeature() };
        AddSide(list);
        return list;
    }

    static partial void AddSide(List<Feature> list);
}
