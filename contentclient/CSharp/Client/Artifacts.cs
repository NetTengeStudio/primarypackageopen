using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;

namespace SubSeal.Client
{
    internal static class Artifacts
    {
        private static readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void Track(string path)
        {
            if (string.IsNullOrEmpty(path)) { return; }
            seen.Add(path);
        }

        internal static void DiscardAll()
        {
            foreach (string path in seen)
            {
                try
                {
                    SubmarineInfo.RemoveSavedSub(path);
                    if (File.Exists(path)) { File.Delete(path); }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            seen.Clear();
        }
    }
}
