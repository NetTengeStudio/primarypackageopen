using System;
using Barotrauma.LuaCs.Data;

namespace PrimaryPackage.Core;

public static class ModConfig {
    private static IConfigService service;

    public static void Mount(IConfigService config) => service = config;

    public static void Release() => service = null;

    public static bool TryGet<T>(string name, out T setting) where T : ISettingBase {
        setting = default;
        if (service == null || ModAssets.Package == null) { return false; }
        if (!service.TryGetConfig(ModAssets.Package, name, out setting)) {
            ModLog.Once($"{name} config is missing");
            return false;
        }
        return true;
    }

    public static void Save(ISettingBase setting) {
        if (service == null || setting == null || setting.IsDisposed) { return; }
        try {
            service.SaveConfigValue(setting);
        } catch (Exception error) {
            ModLog.Once($"{setting.InternalName} config does not save: {error.Message}");
        }
    }
}
