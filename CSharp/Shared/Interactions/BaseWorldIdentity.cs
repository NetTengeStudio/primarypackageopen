using System;
using System.Buffers.Binary;

namespace PrimaryPackage.Interactions;

internal static class BaseWorldIdentity {
    // Native IDs are valid only inside the verified original graph and this random
    // round epoch. Replacements must use a fresh incarnation and explicit binding.
    internal static Guid For(Guid epoch, ushort nativeId) {
        if (epoch == Guid.Empty || nativeId == 0) { throw new ArgumentException("Invalid base-world identity"); }
        Span<byte> bytes = stackalloc byte[16]; epoch.TryWriteBytes(bytes); BinaryPrimitives.WriteUInt16LittleEndian(bytes, nativeId); return new Guid(bytes);
    }
    internal static bool TryResolve(Guid epoch, Guid token, out ushort nativeId) {
        nativeId = 0; if (epoch == Guid.Empty || token == Guid.Empty) { return false; }
        Span<byte> source = stackalloc byte[16], candidate = stackalloc byte[16]; epoch.TryWriteBytes(source); token.TryWriteBytes(candidate);
        if (!source[2..].SequenceEqual(candidate[2..])) { return false; }
        nativeId = BinaryPrimitives.ReadUInt16LittleEndian(candidate); return nativeId != 0;
    }
}
