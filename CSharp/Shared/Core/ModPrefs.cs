using System;
using System.IO;
using System.Xml.Linq;

namespace PrimaryPackage.Core;

public static class ModPrefs {
    public static XElement Root(string relativePath) {
        string path = ModAssets.Resolve(relativePath);
        if (path.Length == 0 || !File.Exists(path)) { return null; }
        try {
            return XDocument.Load(path).Root;
        } catch (Exception error) {
            ModLog.Once($"{relativePath} cannot be read: {error.Message}");
            return null;
        }
    }

    public static bool Read(string relativePath, Action<XElement> apply) {
        XElement root = Root(relativePath);
        if (root == null || apply == null) { return false; }
        try {
            apply(root);
            return true;
        } catch (Exception error) {
            ModLog.Once($"{relativePath} cannot be read: {error.Message}");
            return false;
        }
    }

    public static XElement Open(string relativePath, Func<XElement> create) =>
        Root(relativePath) ?? create?.Invoke();

    public static bool Write(string relativePath, XElement root) {
        if (root == null) { return false; }
        return Stage(relativePath, staging => new XDocument(root).Save(staging));
    }

    public static string WriteText(string relativePath, string text) {
        if (text == null) { return null; }
        string path = ModAssets.Resolve(relativePath);
        return Stage(relativePath, staging => File.WriteAllText(staging, text)) ? path : null;
    }

    private static bool Stage(string relativePath, Action<string> emit) {
        string path = ModAssets.Resolve(relativePath);
        if (path.Length == 0) { return false; }
        string staging = path + ".writing";
        try {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
            emit(staging);
            File.Move(staging, path, overwrite: true);
            return true;
        } catch (Exception error) {
            ModLog.Once($"{relativePath} cannot be written: {error.Message}");
            try {
                if (File.Exists(staging)) { File.Delete(staging); }
            } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("ModPrefs.1", noted); }
            return false;
        }
    }
}
