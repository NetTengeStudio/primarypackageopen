using System.Collections.Generic;
using IEngine.Network;

namespace IEngine;

static partial class FeatureCatalog
{
    static partial void AddSide(List<Feature> list) => list.Add(new EventIndexFeature());
}
