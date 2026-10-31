using System.Collections.Generic;
using Barotrauma;
namespace PrimaryPackage.CrewManifest {
    internal static class SalaryState {
        public static bool PaidThisRound;
        public static readonly HashSet<Identifier> PaidJobs = new();
        public static void Clear() {
            PaidThisRound = false;
            PaidJobs.Clear();
        }
    }
}
