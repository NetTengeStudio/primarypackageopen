using Barotrauma;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
    public static class InspectRuntime {
        private static IInspectSettings playing;
        private static MapEntity unread;

        public static bool AnyPlaying => ScriptToast.InspectActive || InspectWorldText.Playing;

        public static bool IsPlaying(in InspectTarget target) =>
            target.Valid && AnyPlaying && ReferenceEquals(playing, target.Settings);

        public static void Sync() {
            if (AnyPlaying) { return; }
            playing = null;
            unread = null;
        }

        public static void Activate(in InspectTarget target) {
            if (!target.Valid) { return; }
            IInspectSettings settings = target.Settings;
            ToastSpec spec = target.BuildSpec();
            bool dark = InspectLight.IsDark(target);
            if (dark) {
                TypewriterCaches caches = target.Caches;
                spec.Text = TypewriterText.Cached(settings.DarkText, ref caches.RawDark, ref caches.ResolvedDark);
            }
            if (string.IsNullOrWhiteSpace(spec.Text)) { return; }
            playing = settings;
            unread = dark ? null : target.Entity;
            if (settings.Mode == InspectMode.Scripted) {
                InspectWorldText.FadeOut();
                ScriptToast.ShowInspect(spec, settings.AllowSkip);
            } else {
                ScriptToast.StopInspect();
                InspectWorldText.Show(target, spec);
            }
        }

        public static void NoteProgress(TypewriterPlayback playback) {
            if (unread == null) { return; }
            if (playback.Elapsed < playback.Spec.InDuration(playback.CharCount)) { return; }
            InspectSeen.Mark(unread);
            unread = null;
        }
    }

    public static class InspectFields {
        private static readonly string[] scriptedFields = { "MaxWidthPercent", "VerticalOffset" };

        private static readonly string[] selfFields = {
            "WorldMaxWidth", "WorldOffsetX", "WorldOffsetY", "WorldScale",
            "MaxViewDistance", "RequireLineOfSight", "DepthOffset"
        };

        public static void ApplyMode(SerializableEntityEditor editor, InspectMode mode) {
            SetVisible(editor, scriptedFields, mode == InspectMode.Scripted);
            SetVisible(editor, selfFields, mode == InspectMode.SelfInspect);
        }

        private static void SetVisible(SerializableEntityEditor editor, string[] names, bool visible) {
            foreach (string name in names) {
                if (!editor.Fields.TryGetValue(name.ToIdentifier(), out GUIComponent[] fields)) { continue; }
                foreach (GUIComponent field in fields) {
                    GUIComponent row = field is GUITickBox ? field : field.Parent ?? field;
                    row.Visible = visible;
                    row.IgnoreLayoutGroups = !visible;
                }
            }
        }
    }
}
