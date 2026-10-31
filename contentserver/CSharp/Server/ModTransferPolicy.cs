using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using Barotrauma.Networking;

namespace SubSeal.Server
{
    /// <summary>
    /// Refuses vanilla mod downloads that could contain protected submarine layouts.
    /// ModSender copies entire package directories, not just files declared by filelist.xml.
    /// Built once on startup: transfers only perform a HashSet lookup.
    /// </summary>
    internal static class ModTransferPolicy
    {
        private static readonly HashSet<string> allowedArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> deniedArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool protectedMode;

        internal static void Build(Settings settings)
        {
            allowedArchives.Clear();
            deniedArchives.Clear();
            protectedMode = settings.PreviewProtection;
            if (!protectedMode) { return; }

            foreach (ContentPackage package in ContentPackageManager.EnabledPackages.All)
            {
                if (package == ContentPackageManager.VanillaCorePackage || !package.HasMultiplayerSyncedContent) { continue; }

                string archive;
                try
                {
                    archive = ModSender.GetCompressedModPath(package);
                }
                catch (Exception exception)
                {
                    Diagnostics.Fault($"could not identify mod archive for {package.Name}: {exception.GetType().Name}; all mod downloads refused");
                    allowedArchives.Clear();
                    deniedArchives.Clear();
                    return;
                }

                bool unsafePackage = true;
                try
                {
                    if (!string.IsNullOrWhiteSpace(package.Dir) && Directory.Exists(package.Dir))
                    {
                        unsafePackage = ContainsProtectedFiles(package.Dir, settings);
                    }
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
                {
                    Diagnostics.Fault($"could not inspect {package.Name}: {exception.GetType().Name}; mod download refused");
                }

                if (unsafePackage)
                {
                    deniedArchives.Add(archive);
                    Diagnostics.Note($"mod archive {package.Name} contains protected or uninspectable files; downloading disabled");
                }
                else
                {
                    allowedArchives.Add(archive);
                }
            }

            // A collision between generated ModSender names must never allow a blocked archive.
            allowedArchives.ExceptWith(deniedArchives);
        }

        internal static bool Allows(FileTransferType type, string path)
        {
            if (type != FileTransferType.Mod || !protectedMode) { return true; }
            return path is not null && allowedArchives.Contains(path);
        }

        internal static bool RefuseAllModRequestsInRequestOnlyMode => protectedMode;

        internal static void Clear()
        {
            allowedArchives.Clear();
            deniedArchives.Clear();
            protectedMode = false;
        }

        private static bool ContainsProtectedFiles(string directory, Settings settings)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".sub", StringComparison.OrdinalIgnoreCase))
                {
                    if (!settings.IsPublicSubmarine(file)) { return true; }
                }
                // Campaign saves and nested archives may contain .sub files.
                // Without parsing and validating them, treating them as safe is unsound.
                else if (extension.Equals(".save", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
