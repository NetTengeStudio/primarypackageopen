using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;

namespace INet {
    public static unsafe class NetRpc {
        public const string RpcChannel = "PP_INetRpcV1";
        private const byte RpcMagic = 0x52;
        private const byte FlagRequest = 0x01;
        private const byte FlagResponse = 0x02;
        private const byte FlagError = 0x04;
        private const int PendingWindow = 1024;

        private delegate void ResponseDelegate(ReadOnlySpan<byte> data, bool error);
        private delegate void RequestDelegate(IReadMessage msg, ushort reqId, Barotrauma.Networking.Client sender);

        private struct PendingEntry {
            public ushort RequestId;
            public float Expiry;
            public ResponseDelegate Callback;
        }

        private static ushort nextReqId = 1;
        private static readonly PendingEntry[] pending = new PendingEntry[PendingWindow];
        private static readonly Dictionary<ushort, RequestDelegate> handlers = new();
        private static IDisposable listener;

        public static void Initialize() {
            if (listener != null) { return; }
#if CLIENT
            listener = Net.Listen(RpcChannel, OnReceive);
#else
            listener = Net.Listen(RpcChannel, OnReceiveServer);
#endif
        }

        public static void Release() {
            listener?.Dispose();
            listener = null;
            handlers.Clear();
            Array.Clear(pending, 0, pending.Length);
        }

        public static void Update(float currentTime) {
            for (int i = 0; i < PendingWindow; i++) {
                if (pending[i].Callback != null && currentTime >= pending[i].Expiry) {
                    var cb = pending[i].Callback;
                    pending[i] = default;
                    try { cb(ReadOnlySpan<byte>.Empty, true); } catch { }
                }
            }
        }

        public static void RegisterHandler<TReq, TRes>(ushort methodId, Func<TReq, TRes> handler)
            where TReq : unmanaged
            where TRes : unmanaged {
            handlers[methodId] = (msg, reqId, sender) => {
                TReq req = default;
                byte* pReq = (byte*)&req;
                for (int i = 0; i < sizeof(TReq); i++) {
                    pReq[i] = msg.ReadByte();
                }
                TRes res = handler(req);
#if CLIENT
                SendResponse(reqId, methodId, in res);
#else
                if (sender?.Connection != null) {
                    SendResponse(sender.Connection, reqId, methodId, in res);
                }
#endif
            };
        }

#if CLIENT
        public static void Call<TReq, TRes>(ushort methodId, in TReq request, Action<TRes, bool> callback, float timeout = 5.0f, float currentTime = 0.0f)
            where TReq : unmanaged
            where TRes : unmanaged {
            ushort id = nextReqId++;
            if (id == 0) { id = nextReqId++; }
            int slot = id & (PendingWindow - 1);
            pending[slot] = new PendingEntry {
                RequestId = id,
                Expiry = currentTime + timeout,
                Callback = (data, error) => {
                    if (error || data.Length < sizeof(TRes)) {
                        callback(default, false);
                        return;
                    }
                    fixed (byte* p = data) {
                        callback(*(TRes*)p, true);
                    }
                }
            };

            byte[] reqBytes = new byte[sizeof(TReq)];
            fixed (byte* pDst = reqBytes) {
                *(TReq*)pDst = request;
            }
            Net.Send(RpcChannel, msg => {
                msg.WriteByte(RpcMagic);
                msg.WriteByte(FlagRequest);
                msg.WriteUInt16(id);
                msg.WriteUInt16(methodId);
                for (int i = 0; i < reqBytes.Length; i++) {
                    msg.WriteByte(reqBytes[i]);
                }
            });
        }

        private static void SendResponse<TRes>(ushort reqId, ushort methodId, in TRes response) where TRes : unmanaged {
            byte[] resBytes = new byte[sizeof(TRes)];
            fixed (byte* pDst = resBytes) {
                *(TRes*)pDst = response;
            }
            Net.Send(RpcChannel, msg => {
                msg.WriteByte(RpcMagic);
                msg.WriteByte(FlagResponse);
                msg.WriteUInt16(reqId);
                msg.WriteUInt16(methodId);
                for (int i = 0; i < resBytes.Length; i++) {
                    msg.WriteByte(resBytes[i]);
                }
            });
        }

        private static void OnReceive(IReadMessage msg) {
            if (msg.ReadByte() != RpcMagic) { return; }
            byte flags = msg.ReadByte();
            ushort reqId = msg.ReadUInt16();
            ushort methodId = msg.ReadUInt16();

            if ((flags & FlagResponse) != 0) {
                int slot = reqId & (PendingWindow - 1);
                if (pending[slot].RequestId == reqId && pending[slot].Callback != null) {
                    var cb = pending[slot].Callback;
                    pending[slot] = default;
                    int remaining = (msg.LengthBits - msg.BitPosition + 7) / 8;
                    Span<byte> buf = stackalloc byte[Math.Min(512, remaining)];
                    for (int i = 0; i < remaining; i++) {
                        buf[i] = msg.ReadByte();
                    }
                    cb(buf, (flags & FlagError) != 0);
                }
            } else if ((flags & FlagRequest) != 0) {
                if (handlers.TryGetValue(methodId, out var h)) {
                    try { h(msg, reqId, null); } catch { }
                }
            }
        }
#else
        public static void Call<TReq, TRes>(NetworkConnection connection, ushort methodId, in TReq request, Action<TRes, bool> callback, float timeout = 5.0f, float currentTime = 0.0f)
            where TReq : unmanaged
            where TRes : unmanaged {
            if (connection == null) { return; }
            ushort id = nextReqId++;
            if (id == 0) { id = nextReqId++; }
            int slot = id & (PendingWindow - 1);
            pending[slot] = new PendingEntry {
                RequestId = id,
                Expiry = currentTime + timeout,
                Callback = (data, error) => {
                    if (error || data.Length < sizeof(TRes)) {
                        callback(default, false);
                        return;
                    }
                    fixed (byte* p = data) {
                        callback(*(TRes*)p, true);
                    }
                }
            };

            byte[] reqBytes = new byte[sizeof(TReq)];
            fixed (byte* dst = reqBytes) {
                *(TReq*)dst = request;
            }
            Net.Send(RpcChannel, connection, msg => {
                msg.WriteByte(RpcMagic);
                msg.WriteByte(FlagRequest);
                msg.WriteUInt16(id);
                msg.WriteUInt16(methodId);
                msg.WriteBytes(reqBytes, 0, reqBytes.Length);
            });
        }

        private static void SendResponse<TRes>(NetworkConnection conn, ushort reqId, ushort methodId, in TRes response) where TRes : unmanaged {
            byte[] resBytes = new byte[sizeof(TRes)];
            fixed (byte* dst = resBytes) {
                *(TRes*)dst = response;
            }
            Net.Send(RpcChannel, conn, msg => {
                msg.WriteByte(RpcMagic);
                msg.WriteByte(FlagResponse);
                msg.WriteUInt16(reqId);
                msg.WriteUInt16(methodId);
                msg.WriteBytes(resBytes, 0, resBytes.Length);
            });
        }

        private static void OnReceiveServer(IReadMessage msg, Barotrauma.Networking.Client sender) {
            if (msg.ReadByte() != RpcMagic || sender == null) { return; }
            byte flags = msg.ReadByte();
            ushort reqId = msg.ReadUInt16();
            ushort methodId = msg.ReadUInt16();

            if ((flags & FlagResponse) != 0) {
                int slot = reqId & (PendingWindow - 1);
                if (pending[slot].RequestId == reqId && pending[slot].Callback != null) {
                    var cb = pending[slot].Callback;
                    pending[slot] = default;
                    int remaining = (msg.LengthBits - msg.BitPosition + 7) / 8;
                    Span<byte> buf = stackalloc byte[Math.Min(512, remaining)];
                    for (int i = 0; i < remaining; i++) {
                        buf[i] = msg.ReadByte();
                    }
                    cb(buf, (flags & FlagError) != 0);
                }
            } else if ((flags & FlagRequest) != 0) {
                if (handlers.TryGetValue(methodId, out var h)) {
                    try { h(msg, reqId, sender); } catch { }
                }
            }
        }
#endif
    }
}