using System;
using System.Collections.Generic;

namespace PrimaryPackage.Interactions;

internal sealed class LegacyDirectorScene {
    public string Name { get; set; } = "";
    public List<LegacyDirectorEffect> Events { get; set; } = new();
}
internal sealed class LegacyDirectorEffect {
    public int Kind { get; set; }
    public string Target { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Visual { get; set; } = "";
    public string Text { get; set; } = "";
    public float Gain { get; set; } = .35f;
    public float Duration { get; set; } = 5;
    public float Interval { get; set; } = 2;
    public float Delay { get; set; }
}
internal static class LegacyScenes {
    internal static List<SceneProgram> Convert(List<LegacyDirectorScene> legacy) {
        if (legacy == null || legacy.Count > 32) { throw new ArgumentException("Недопустимая библиотека старых сценариев"); }
        var result = new List<SceneProgram>(legacy.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in legacy) {
            if (scene == null || scene.Events == null || !names.Add(scene.Name)) { throw new ArgumentException("Повреждён старый сценарий"); }
            var program = new SceneProgram { Name = scene.Name };
            string target = null;
            foreach (var effect in scene.Events) {
                if (effect == null || effect.Target == null || effect.Kind is < 0 or > 2) { throw new ArgumentException("Недопустимое действие старого сценария"); }
                if (effect.Target.Length > 0) {
                    if (target != null && target != effect.Target) { throw new ArgumentException("Старый сценарий «" + scene.Name + "» содержит несколько персональных целей. Нужен ручной перенос привязок; исходный файл сохранён"); }
                    target = effect.Target;
                }
                program.Steps.Add(new SceneStep { Action = (SceneAction)effect.Kind, Audience = effect.Kind == 2 && effect.Target.Length == 0 ? SceneAudience.Everyone : SceneAudience.SelectedPlayer,
                    Delay = effect.Delay, Duration = effect.Duration, Gain = effect.Gain, Interval = effect.Interval, Resource = effect.Resource, Visual = effect.Visual, Text = effect.Text });
            }
            program.Validate(); result.Add(program);
        }
        return result;
    }
}
