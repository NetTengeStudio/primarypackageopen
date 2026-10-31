using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;

namespace SubSeal.Server
{
    internal sealed class Settings
    {
        internal bool RestrictToActiveSubmarines { get; private set; }

        internal bool PreviewProtection { get; private set; } = true;

        internal bool AllowUnsealedForIncapableClients { get; private set; }

        internal bool AllowPlaintextWhenUnenforceable { get; private set; }

        internal double LeaseRetentionSeconds { get; private set; } = 30.0;

        internal double BudgetCapacity { get; private set; } = 8.0;

        internal double BudgetRefillSeconds { get; private set; } = 30.0;

        internal double BudgetRetentionSeconds { get; private set; } = 900.0;

        internal double BudgetRefillPerSecond => 1.0 / BudgetRefillSeconds;

        internal bool IsPublicSubmarine(string filePath)
        {
            if (publicSubmarines.Count == 0) { return false; }
            string name = System.IO.Path.GetFileNameWithoutExtension(filePath);
            return name is not null && publicSubmarines.Contains(name);
        }

        private readonly HashSet<string> publicSubmarines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static Settings Load(string packageDirectory)
        {
            Settings settings = new Settings();

            if (string.IsNullOrEmpty(packageDirectory))
            {
                Diagnostics.Fault("package directory unknown, defaults applied");
                return settings;
            }

            string path = System.IO.Path.Combine(packageDirectory, "settings.xml");

            if (!System.IO.File.Exists(path))
            {
                Diagnostics.Fault("settings.xml missing, defaults applied");
                return settings;
            }

            try
            {
                XElement root = XDocument.Load(path).Root;
                if (root is null) { return settings; }

                settings.RestrictToActiveSubmarines = root.GetAttributeBool(nameof(RestrictToActiveSubmarines), settings.RestrictToActiveSubmarines);
                settings.PreviewProtection = root.GetAttributeBool(nameof(PreviewProtection), settings.PreviewProtection);
                settings.AllowUnsealedForIncapableClients = root.GetAttributeBool(nameof(AllowUnsealedForIncapableClients), settings.AllowUnsealedForIncapableClients);
                settings.AllowPlaintextWhenUnenforceable = root.GetAttributeBool(nameof(AllowPlaintextWhenUnenforceable), settings.AllowPlaintextWhenUnenforceable);
                settings.LeaseRetentionSeconds = Math.Max(1.0, root.GetAttributeDouble(nameof(LeaseRetentionSeconds), settings.LeaseRetentionSeconds));
                settings.BudgetCapacity = Math.Max(1.0, root.GetAttributeDouble(nameof(BudgetCapacity), settings.BudgetCapacity));
                settings.BudgetRefillSeconds = Math.Max(1.0, root.GetAttributeDouble(nameof(BudgetRefillSeconds), settings.BudgetRefillSeconds));
                settings.BudgetRetentionSeconds = Math.Max(settings.BudgetCapacity * settings.BudgetRefillSeconds, root.GetAttributeDouble(nameof(BudgetRetentionSeconds), settings.BudgetRetentionSeconds));

                string publicNames = root.GetAttributeString("PublicSubmarines", "");
                foreach (string name in publicNames.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = name.Trim();
                    if (trimmed.Length == 0) { continue; }
                    settings.publicSubmarines.Add(trimmed);
                }
            }
            catch (Exception)
            {
                Diagnostics.Fault("settings.xml unreadable");
            }

            return settings;
        }
    }
}
