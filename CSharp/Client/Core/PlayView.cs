namespace PrimaryPackage.Core;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif

public static class PlayView {
    public static bool Busy => GUI.InputBlockingMenuOpen || CharacterHealth.OpenHealthWindow != null || PrimaryPackage.GUI.GUIBridge.CapturesViewInput;

    public static bool Typing => GUI.KeyboardDispatcher.Subscriber != null;

    public static bool Editing => Screen.Selected is { IsEditor: true };

    public static bool Running =>
        GameMain.GameSession != null && GameMain.GameSession.IsRunning &&
        Screen.Selected == GameMain.GameScreen;

    public static bool Active(Character character) =>
        Running && character != null && !character.Removed && !character.IsDead;

    public static bool Controls => Running && !Busy && !Typing && Active(Character.Controlled);
}
