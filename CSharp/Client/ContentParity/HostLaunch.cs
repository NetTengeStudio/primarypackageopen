using System;
using System.Diagnostics;
using System.IO;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public static class HostLaunch {
    public static void Attach(Harmony harmony) =>
        harmony.Patch(
            AccessTools.Method(typeof(ChildServerRelay), nameof(ChildServerRelay.Start), new[] { typeof(ProcessStartInfo) }),
            prefix: ModuleHost.Bind(typeof(HostLaunch), nameof(Prepare)));

    public static void Prepare(ProcessStartInfo processInfo) {
        if (processInfo == null) { return; }
        try {
            GameSettings.SaveCurrentConfig();
        } catch (Exception) {
            ModLog.Error("player config is not saved before hosting");
        }
        string client = BuildRevision.Current;
        string directory = string.IsNullOrEmpty(processInfo.WorkingDirectory) ? Directory.GetCurrentDirectory() : processInfo.WorkingDirectory;
        string chosen = BuildRevision.Of(AssemblyOf(processInfo.FileName, directory));
        if (BuildRevision.Same(chosen, client)) { return; }
        string root = RootExecutable(directory, client);
        if (root == null) {
            ModLog.Once($"server build {BuildRevision.Short(chosen)} differs from client build {BuildRevision.Short(client)}");
            return;
        }
        processInfo.FileName = root;
        ModLog.Message($"server starts from client build {BuildRevision.Short(client)} instead of {BuildRevision.Short(chosen)}");
    }

    private static string AssemblyOf(string fileName, string directory) {
        if (string.IsNullOrEmpty(fileName)) { return string.Empty; }
        string path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(directory, fileName);
        return Path.ChangeExtension(path, ".dll");
    }

    private static string RootExecutable(string directory, string client) {
        if (!BuildRevision.Same(BuildRevision.Of(Path.Combine(directory, "DedicatedServer.dll")), client)) { return null; }
        bool windows = OperatingSystem.IsWindows();
        if (!File.Exists(Path.Combine(directory, windows ? "DedicatedServer.exe" : "DedicatedServer"))) { return null; }
        return windows ? "DedicatedServer.exe" : "./DedicatedServer";
    }
}
