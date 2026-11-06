using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace INet;

public static class BuildRevision {
    private static readonly Lazy<string> engineGitRevision = new(() => {
        try {
            return typeof(Barotrauma.GameMain).Assembly.GetType("AssemblyInfo")
                ?.GetField("GitRevision", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as string ?? string.Empty;
        } catch (Exception) {
            return string.Empty;
        }
    });

    public static string Current => engineGitRevision.Value;

    public static string Of(string assemblyPath) {
        try {
            return File.Exists(assemblyPath) ? Parse(FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion) : string.Empty;
        } catch (Exception) {
            return string.Empty;
        }
    }

    public static string Parse(string informational) {
        if (string.IsNullOrEmpty(informational)) { return string.Empty; }
        int plus = informational.IndexOf('+');
        return plus < 0 ? string.Empty : informational.Substring(plus + 1).Trim();
    }

    public static bool Same(string a, string b) {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) { return false; }
        int length = Math.Min(a.Length, b.Length);
        return length >= 7 && string.Compare(a, 0, b, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    public static string Short(string revision) =>
        string.IsNullOrEmpty(revision) ? "unknown" : revision.Length > 10 ? revision.Substring(0, 10) : revision;
}
