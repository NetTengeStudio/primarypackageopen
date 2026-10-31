namespace PrimaryPackage.Core;

public readonly struct BitField {
    public readonly byte Offset;
    public readonly byte Width;
    public readonly uint Mask;

    public BitField(int offset, int width) {
        Offset = (byte)offset;
        Width = (byte)width;
        Mask = width >= 32 ? uint.MaxValue : (1u << width) - 1u;
    }

    public uint Read(uint word) => (word >> Offset) & Mask;

    public uint Write(uint word, uint value) =>
        (word & ~(Mask << Offset)) | ((value & Mask) << Offset);

    public uint Quantize(float unit) {
        float scaled = unit * Mask + 0.5f;
        if (scaled <= 0.0f) { return 0u; }
        uint raw = (uint)scaled;
        return raw > Mask ? Mask : raw;
    }

    public float Dequantize(uint raw) => raw * (1.0f / Mask);

    public bool Holds(int value) => value >= 0 && (uint)value <= Mask;
}
