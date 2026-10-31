namespace PrimaryPackage.Cold {
    public static class ColdNet {
        public const string StateId = "PP_ColdState";
        public const string RequestId = "PP_ColdRequest";

        public const byte FlagFull = 1;
        // Explicit completion marker for an ordered multipart full snapshot.
        public const byte FlagSnapshotComplete = 2;
        public const int MaxPerMessage = 220;

        private static readonly PrimaryPackage.Core.BitField temperature = new PrimaryPackage.Core.BitField(0, 8);

        public static byte Quantize(float value) {
            float min = ColdConfig.MinTemperature;
            float span = ColdConfig.MaxTemperature - min;
            if (span <= 0.0f) { return 0; }
            return (byte)temperature.Quantize((value - min) / span);
        }

        public static float Dequantize(byte raw) {
            float min = ColdConfig.MinTemperature;
            return min + (ColdConfig.MaxTemperature - min) * temperature.Dequantize(raw);
        }
    }
}
