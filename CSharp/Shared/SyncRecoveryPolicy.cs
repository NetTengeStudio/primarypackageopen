namespace PrimaryPackage.Netcode {
    public static class SyncRecoveryPolicy {
        public const double ProgressGraceSeconds = 10.0;
        public const double AbsoluteCeilingSeconds = 600.0;
        public const double SyncExtendSeconds = 15.0;
        public const double HealthyResetSeconds = 10.0;

        public static bool IsProgressing(double now, double lastProgressTime) =>
            now - lastProgressTime < ProgressGraceSeconds;

        public static bool ShouldVeto(double now, double lastProgressTime, double recoveryStartTime) =>
            IsProgressing(now, lastProgressTime) && now - recoveryStartTime < AbsoluteCeilingSeconds;
    }
}
