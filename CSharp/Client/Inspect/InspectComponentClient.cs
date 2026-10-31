using Barotrauma;
using PrimaryPackage.Fonts;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public partial class InspectComponent {
        internal readonly TypewriterCaches Caches = new TypewriterCaches();

        public ToastSpec BuildSpec() => TypewriterSpec.Build(this, item.Prefab.ContentPackage, Caches);

        public override void CreateEditingHUD(SerializableEntityEditor editor) {
            InspectFields.ApplyMode(editor, Mode);
            FontFieldUI.Apply(editor, this);
        }

        partial void ModeChangedProjSpecific() {
            if (item == null || Screen.Selected != GameMain.SubEditorScreen || !item.IsSelected) { return; }
            item.CreateEditingHUD();
        }
    }
}
