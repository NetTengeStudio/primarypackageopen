namespace PrimaryPackage.Kits;

internal static class KitMenu {
    public static void Toggle() => Interactions.InteractionClient.Instance?.ToggleStudio();
    public static void OnKitsChanged() => Interactions.InteractionClient.Instance?.RefreshStudioKits();
}
