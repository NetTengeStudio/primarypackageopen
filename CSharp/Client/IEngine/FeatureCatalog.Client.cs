using System.Collections.Generic;
using IEngine.Render;
using IEngine.Execution;

namespace IEngine;

static partial class FeatureCatalog
{
    static partial void AddSide(List<Feature> list) => list.AddRange(new Feature[] { new RenderFeature(), new ShadowFeature(), new SpriteTraversalFeature() });
}
