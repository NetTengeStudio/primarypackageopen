using Barotrauma;
using PrimaryPackage.Zones;
using PrimaryPackage.Core;

namespace PrimaryPackage.Inspect {
    public readonly struct InspectTarget {
        public readonly IInspectSettings Settings;
        public readonly MapEntity Entity;
        public readonly TypewriterCaches Caches;

        public InspectTarget(IInspectSettings settings, MapEntity entity, TypewriterCaches caches) {
            Settings = settings;
            Entity = entity;
            Caches = caches;
        }

        public bool Valid => Settings != null && Entity != null && !Entity.Removed;

        public bool Same(in InspectTarget other) => ReferenceEquals(Settings, other.Settings);

        public ToastSpec BuildSpec() => TypewriterSpec.Build(Settings, InspectPackage.Own, Caches);
    }

    public static class InspectPackage {
        public static ContentPackage Own => ModAssets.Package;
    }
}
