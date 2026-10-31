namespace PrimaryPackage.WallCollision {
    public static class WallCollisionNet {
        public const string StateId = "PP_WallCollisionState";
        public const string RequestId = "PP_WallCollisionRequest";

        public static ushort Revision { get; private set; }

        public static ushort Advance() {
            Revision = unchecked((ushort)(Revision + 1));
            return Revision;
        }

        public static void Accept(ushort revision) => Revision = revision;
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
