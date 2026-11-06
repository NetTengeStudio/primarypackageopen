using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;

namespace INet {
    public static unsafe class NetMultiplexer {
        public const string MuxChannel = "PP_INetMuxV1";
        private const byte MuxMagic = 0x6D;
        private const int MaxBatchBytes = 1100;

        private static readonly byte[] outgoingBuffer = new byte[MaxBatchBytes];
        private static int outgoingBytes = 0;
        private static int outgoingPacketCount = 0;
        private static IDisposable listener;

#if CLIENT
        public delegate void MuxHandlerClient(ReadOnlySpan<byte> payload);
        private static readonly Dictionary<ushort, MuxHandlerClient> handlers = new();
#else
        public delegate void MuxHandlerServer(ReadOnlySpan<byte> payload, Barotrauma.Networking.Client sender);
        private static readonly Dictionary<ushort, MuxHandlerServer> handlers = new();
#endif

        public static void Initialize() {
            if (listener != null) { return; }
#if CLIENT
            listener = Net.Listen(MuxChannel, OnReceiveFrame);
#else
            listener = Net.Listen(MuxChannel, OnReceiveFrameServer);
#endif
        }

        public static void Release() {
            listener?.Dispose();
            listener = null;
            handlers.Clear();
            outgoingBytes = 0;
            outgoingPacketCount = 0;
        }

#if CLIENT
        public static void Register(ushort channelId, MuxHandlerClient handler) {
            ArgumentNullException.ThrowIfNull(handler);
            handlers[channelId] = handler;
        }
#else
        public static void Register(ushort channelId, MuxHandlerServer handler) {
            ArgumentNullException.ThrowIfNull(handler);
            handlers[channelId] = handler;
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Enqueue(ushort channelId, ReadOnlySpan<byte> payload) {
            int needed = 4 + 4 + payload.Length;
            if (needed > MaxBatchBytes - 8) {
                return false;
            }
            if (outgoingBytes + needed > MaxBatchBytes) {
                Flush();
            }
            fixed (byte* ptr = outgoingBuffer) {
                ptr[outgoingBytes++] = (byte)(channelId & 0xFF);
                ptr[outgoingBytes++] = (byte)((channelId >> 8) & 0xFF);
                ptr[outgoingBytes++] = (byte)(payload.Length & 0xFF);
                ptr[outgoingBytes++] = (byte)((payload.Length >> 8) & 0xFF);
            }
            payload.CopyTo(outgoingBuffer.AsSpan(outgoingBytes));
            outgoingBytes += payload.Length;
            outgoingPacketCount++;
            return true;
        }

        public static void Flush(DeliveryMethod delivery = DeliveryMethod.Unreliable) {
            if (outgoingPacketCount == 0) { return; }
            int count = outgoingPacketCount;
            int totalBytes = outgoingBytes;
            outgoingPacketCount = 0;
            outgoingBytes = 0;

#if CLIENT
            Net.Send(MuxChannel, msg => {
                msg.WriteByte(MuxMagic);
                NativeWire.WriteVarUInt32(msg, (uint)count);
                for (int i = 0; i < totalBytes; i++) {
                    msg.WriteByte(outgoingBuffer[i]);
                }
            }, delivery);
#else
            Net.Broadcast(MuxChannel, msg => {
                msg.WriteByte(MuxMagic);
                NativeWire.WriteVarUInt32(msg, (uint)count);
                for (int i = 0; i < totalBytes; i++) {
                    msg.WriteByte(outgoingBuffer[i]);
                }
            }, delivery, NetAudience.InGame);
#endif
        }

#if CLIENT
        private static void OnReceiveFrame(IReadMessage msg) {
            if (msg.ReadByte() != MuxMagic) { return; }
            int count = (int)NativeWire.ReadVarUInt32(msg);
            Span<byte> temp = stackalloc byte[512];
            for (int i = 0; i < count; i++) {
                ushort ch = msg.ReadUInt16();
                ushort len = msg.ReadUInt16();
                Span<byte> target = len <= temp.Length ? temp.Slice(0, len) : new byte[len];
                for (int b = 0; b < len; b++) {
                    target[b] = msg.ReadByte();
                }
                if (handlers.TryGetValue(ch, out var handler)) {
                    try { handler(target); } catch { }
                }
            }
        }
#else
        private static void OnReceiveFrameServer(IReadMessage msg, Barotrauma.Networking.Client sender) {
            if (msg.ReadByte() != MuxMagic || sender == null) { return; }
            int count = (int)NativeWire.ReadVarUInt32(msg);
            Span<byte> temp = stackalloc byte[512];
            for (int i = 0; i < count; i++) {
                ushort ch = msg.ReadUInt16();
                ushort len = msg.ReadUInt16();
                Span<byte> target = len <= temp.Length ? temp.Slice(0, len) : new byte[len];
                for (int b = 0; b < len; b++) {
                    target[b] = msg.ReadByte();
                }
                if (handlers.TryGetValue(ch, out var handler)) {
                    try { handler(target, sender); } catch { }
                }
            }
        }
#endif
    }
}