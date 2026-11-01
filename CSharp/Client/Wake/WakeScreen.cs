using Barotrauma;

namespace PrimaryPackage.Wake;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif

public static class WakeScreen {
    private static bool held;
    private static bool hud, upperHud, highlights, names;

    public static bool Held => held;

    public static void Hold() {
        if (held) { return; }
        held = true;
        hud = GUI.DisableHUD;
        upperHud = GUI.DisableUpperHUD;
        highlights = GUI.DisableItemHighlights;
        names = GUI.DisableCharacterNames;
        GUI.DisableHUD = true;
        GUI.DisableUpperHUD = true;
        GUI.DisableItemHighlights = true;
        GUI.DisableCharacterNames = true;
    }

    public static void Give() {
        if (!held) { return; }
        held = false;
        if (GUI.DisableHUD) { GUI.DisableHUD = hud; }
        if (GUI.DisableUpperHUD) { GUI.DisableUpperHUD = upperHud; }
        if (GUI.DisableItemHighlights) { GUI.DisableItemHighlights = highlights; }
        if (GUI.DisableCharacterNames) { GUI.DisableCharacterNames = names; }
    }
}
