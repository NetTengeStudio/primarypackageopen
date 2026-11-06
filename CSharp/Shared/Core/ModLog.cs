using System.Collections.Generic;

namespace PrimaryPackage.Core;

public static class ModLog {
    private static readonly HashSet<string> spoken = new HashSet<string>();

    private static ILoggerService mounted;

    public static void Mount(ILoggerService logger) => mounted = logger;

    private static readonly Dictionary<string, long> throttled = new Dictionary<string, long>();

    public static void Release() {
        spoken.Clear();
        throttled.Clear();
        mounted = null;
    }

    public static void Throttled(string key, string text, long intervalMs = 5000) {
        long now = System.Environment.TickCount64;
        if (throttled.TryGetValue(key, out long next) && now < next) { return; }
        throttled[key] = now + intervalMs;
        Error(text);
    }

    public static void Message(string text) => Service()?.LogMessage(text);

    public static void Warning(string text) => Service()?.LogWarning(text);

    public static void Error(string text) => Service()?.LogError(text);

    public static void Once(string text) {
        if (!spoken.Add(text)) { return; }
        Error(text);
    }

    private static ILoggerService Service() {
        if (mounted != null) { return mounted; }
        try {
            return LuaCsSetup.Instance?.Logger;
        } catch {
            return null;
        }
    }
}
