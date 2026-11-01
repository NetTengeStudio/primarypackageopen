using System.IO;

namespace PrimaryPackage.Core;

public static class ModAssets {
    private static ContentPackage mounted;
    private static string root = string.Empty;

    public static string Root => root;

    public static ContentPackage Package => mounted;

    public static bool Mount<TPlugin>(IPluginManagementService plugins) where TPlugin : IAssemblyPlugin {
        if (plugins == null) { return false; }
        if (!plugins.TryGetPackageForPlugin<TPlugin>(out ContentPackage package) || package == null) {
            return false;
        }
        return Mount(package);
    }

    public static bool Mount(ContentPackage package) {
        if (package == null || string.IsNullOrEmpty(package.Dir)) { return false; }
        mounted = package;
        root = package.Dir;
        return true;
    }

    public static void Release() {
        mounted = null;
        root = string.Empty;
    }

    public static string Resolve(string relativePath) {
        if (string.IsNullOrEmpty(relativePath)) { return string.Empty; }
        if (root.Length == 0) {
            ModLog.Once("mod root is not mounted");
            return string.Empty;
        }
        return Path.Combine(root, relativePath);
    }

    public static bool Exists(string relativePath) {
        string absolute = Resolve(relativePath);
        return absolute.Length > 0 && File.Exists(absolute);
    }
}
