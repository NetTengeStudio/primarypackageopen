using System;
using PrimaryPackage.Core.Fault;

namespace PrimaryPackage.Fault;

public static class ModFault {
    public static void Note(string where, Exception error) {
        try {
            FaultSite site = FaultHooks.Ledger.Register("best-effort " + where, FaultPolicy.Essential, false);
            FaultHooks.Ledger.Record(site, null, null, error, Environment.TickCount64 / 1000.0);
        } catch (Exception) {
            return;
        }
    }
}
