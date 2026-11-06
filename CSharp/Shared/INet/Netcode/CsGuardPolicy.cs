namespace INet {
    public enum CsGuardVerdict {
        Wait,
        Drop,
        Reanchor,
        Probe,
        Kick
    }

    public static class CsGuardPolicy {
        public const double GraceSeconds = 150.0;
        public const double ProbeSeconds = 45.0;

        public static CsGuardVerdict Decide(double now, double since, double probedAt, bool exempt, bool settling) {
            if (exempt) { return CsGuardVerdict.Drop; }
            if (settling) { return CsGuardVerdict.Reanchor; }
            if (double.IsNaN(probedAt)) {
                return now - since > GraceSeconds ? CsGuardVerdict.Probe : CsGuardVerdict.Wait;
            }
            return now - probedAt > ProbeSeconds ? CsGuardVerdict.Kick : CsGuardVerdict.Wait;
        }
    }
}
