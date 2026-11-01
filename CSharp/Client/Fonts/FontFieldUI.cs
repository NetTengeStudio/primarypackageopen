using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Fonts {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public static class FontFieldUI {
        private const string PropertyName = "FontName";

        public static void Apply(SerializableEntityEditor editor, ISerializableEntity entity) {
            if (editor == null || entity == null) { return; }
            if (!entity.SerializableProperties.TryGetValue(PropertyName.ToIdentifier(), out SerializableProperty property)) { return; }
            if (!editor.Fields.TryGetValue(PropertyName.ToIdentifier(), out GUIComponent[] fields)) { return; }

            foreach (GUIComponent field in fields) {
                GUIComponent row = field.Parent ?? field;
                row.Visible = false;
                row.IgnoreLayoutGroups = true;
            }

            int height = (int)(24 * GUI.Scale);
            var frame = new GUIFrame(new RectTransform(new Point(editor.Rect.Width, height), isFixedSize: true), color: Color.Transparent);
            new GUITextBlock(new RectTransform(new Vector2(0.4f, 1.0f), frame.RectTransform),
                TextManager.Get("sp.inspectcomponent.fontname.name").Fallback("Шрифт"), font: GUIStyle.SmallFont);
            var dropDown = new GUIDropDown(new RectTransform(new Vector2(0.6f, 1.0f), frame.RectTransform, Anchor.TopRight)) {
                ToolTip = TextManager.Get("sp.inspectcomponent.fontname.description")
            };

            string current = property.GetValue(entity) as string ?? FontRef.Default;
            FontRef reference = FontRef.Parse(current);
            int selected = 0;
            int index = 0;
            foreach (Identifier name in GUIStyle.Fonts.Keys) {
                string value = FontRef.Compose(false, name);
                dropDown.AddItem("● " + name.Value, value);
                if (!reference.IsFile && reference.Name == name) { selected = index; }
                index++;
            }
            foreach (Identifier name in FontRegistry.Names) {
                string value = FontRef.Compose(true, name);
                dropDown.AddItem("◆ " + name.Value, value);
                if (reference.IsFile && reference.Name == name) { selected = index; }
                index++;
            }
            if (dropDown.CountChildren > 0) { dropDown.SelectItem(dropDown.ListBox.Content.GetChild(selected)?.UserData); }
            dropDown.OnSelected = (component, userData) => {
                property.TrySetValue(entity, userData as string);
                return true;
            };
            editor.AddCustomContent(frame, editor.ContentCount);
        }
    }
}
