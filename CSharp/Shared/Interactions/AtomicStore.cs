using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PrimaryPackage.Interactions;

internal sealed class AtomicStore<T> {
    private sealed class Envelope {
        public int Version { get; set; }
        public string Hash { get; set; }
        public byte[] Payload { get; set; }
    }
    private readonly string path;
    private byte[] committed;
    private readonly Action<T> validate;
    public AtomicStore(string path, Action<T> validate = null) { this.path = path; this.validate = validate; }
    public T Load(Func<T> create, Action<Exception> recovered) {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) { return create(); }
        try { return Read(path, out committed); }
        catch (Exception primary) when (primary is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or CryptographicException) {
            T backup = Read(path + ".bak", out committed);
            recovered(primary);
            return backup;
        }
    }
    private T Read(string source, out byte[] encoded) {
        var file = new FileInfo(source);
        if (file.Length > 16 * 1024 * 1024) { throw new InvalidDataException("Store exceeds size limit"); }
        encoded = File.ReadAllBytes(source);
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(encoded);
        if (envelope == null || envelope.Version != 1 || envelope.Payload == null || envelope.Hash == null ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(envelope.Payload)), envelope.Hash, StringComparison.Ordinal)) {
            throw new InvalidDataException("Invalid store version or checksum");
        }
        T value = JsonSerializer.Deserialize<T>(envelope.Payload) ?? throw new InvalidDataException("Missing store payload");
        validate?.Invoke(value);
        return value;
    }
    public void Save(T value) {
        validate?.Invoke(value);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new Envelope { Version = 1, Payload = payload, Hash = Convert.ToHexString(SHA256.HashData(payload)) });
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".pending";
        Write(temporary, encoded);
        if (committed != null) {
            string backup = path + ".bak.pending";
            Write(backup, committed);
            File.Move(backup, path + ".bak", true);
        }
        File.Move(temporary, path, true);
        committed = encoded;
    }
    private static void Write(string path, byte[] encoded) {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(encoded);
        stream.Flush(true);
    }
}
