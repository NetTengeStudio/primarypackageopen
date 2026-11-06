using System.Collections.Generic;
using INet;

namespace IEngine;

static partial class FeatureCatalog
{
    static partial void AddSide(List<Feature> list) => list.Add(new EventIndexFeature());
}
