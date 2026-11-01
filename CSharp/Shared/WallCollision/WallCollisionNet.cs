namespace PrimaryPackage.WallCollision {
    public static class WallCollisionNet {
        public const string StateId = "PP_WallCollisionStateV3";
        public const string RequestId = "PP_WallCollisionRequestV3";
        public const int MaxRules = 8192;

        public static uint Epoch { get; private set; }
        public static uint Revision { get; private set; }

        public static void BeginEpoch(uint epoch) {
            Epoch = epoch;
            Revision = 0;
        }

        public static uint Advance() {
            Revision = unchecked(Revision + 1u);
            return Revision;
        }

        public static void Accept(uint revision) => Revision = revision;
    }

    public enum WallMode : byte {
        None = 0,
        Solid = 1,
        OneWay = 2
    }

    public enum WallFace : byte {
        PlusX = 0,
        MinusX = 1,
        PlusY = 2,
        MinusY = 3
    }

    internal enum WallPacket : byte {
        Snapshot = 0,
        Set = 1
    }
}
