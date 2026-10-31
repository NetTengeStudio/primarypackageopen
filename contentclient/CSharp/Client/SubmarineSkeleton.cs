using System.Collections.Generic;
using System.Xml.Linq;

namespace SubSeal.Client
{
    internal static class SubmarineSkeleton
    {
        private const string IdAttribute = "ID";

        internal static void ReduceInPlace(XElement source)
        {
            if (source is null) { return; }

            foreach (XElement child in source.Elements())
            {
                child.RemoveNodes();

                List<XAttribute> drop = null;
                foreach (XAttribute attribute in child.Attributes())
                {
                    if (attribute.Name.LocalName == IdAttribute) { continue; }
                    drop ??= new List<XAttribute>();
                    drop.Add(attribute);
                }

                if (drop is null) { continue; }
                foreach (XAttribute attribute in drop)
                {
                    attribute.Remove();
                }
            }
        }

        internal static bool IsReduced(XElement candidate)
        {
            if (candidate is null) { return true; }

            foreach (XElement child in candidate.Elements())
            {
                if (child.HasElements) { return false; }
                foreach (XAttribute attribute in child.Attributes())
                {
                    if (attribute.Name.LocalName != IdAttribute) { return false; }
                }
            }

            return true;
        }
    }
}
