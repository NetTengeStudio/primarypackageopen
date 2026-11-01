using Barotrauma;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public interface ITypewriterPreview {
        MapEntity PreviewEntity { get; }
        ToastSpec BuildSpec();
    }

    public static class InspectSource {
        private sealed class TargetPreview : ITypewriterPreview {
            private InspectTarget target;
            public void Set(in InspectTarget value) => target = value;
            public MapEntity PreviewEntity => target.Entity;
            public ToastSpec BuildSpec() => target.BuildSpec();
        }

        private static readonly TargetPreview preview = new TargetPreview();

        public static InspectTarget For(MapEntity entity) {
            if (entity == null || entity.Removed) { return default; }
            InspectData data = InspectRegistry.Get(entity);
            if (data != null) { return new InspectTarget(data, entity, data.Caches); }
            if (entity is Item item) {
                InspectComponent component = item.GetComponent<InspectComponent>();
                if (component != null) { return new InspectTarget(component, item, component.Caches); }
            }
            return default;
        }

        public static ITypewriterPreview Preview(in InspectTarget target) {
            preview.Set(target);
            return preview;
        }
    }
}
