using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace INet;

public enum NetAudience : byte { All, InGame }

public interface INetPacket
{
    int SizeHintBytes { get; }
    void Write(IWriteMessage output);
}

public sealed class NetChannel
{
    internal readonly NetworkingService.NetId Key;
    internal int LastPayloadBytes;
    public string Id { get; }
    internal NetChannel(string id) { Id = id; Key = new NetworkingService.NetId(id); }
}

public static class Net
{
    static readonly Dictionary<string, Receiver> receivers = new(StringComparer.Ordinal);
    static readonly Dictionary<string, NetChannel> channels = new(StringComparer.Ordinal);
    public static bool Live { get; private set; }
    internal static bool CompactState = true;
    internal static bool CachedChannels = true;
    public static long SentMessages { get; private set; }
    public static long SentBits { get; private set; }
    public static long ReceivedMessages { get; private set; }
    public static long Failures { get; private set; }
    public static long RateLimited { get; private set; }

    public static NetChannel Channel(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!channels.TryGetValue(id, out NetChannel channel)) { channels.Add(id, channel = new NetChannel(id)); }
        return channel;
    }

    public static void Mount() => Live = true;

    public static void Release()
    {
        Live = false;
        foreach (Receiver receiver in receivers.Values) { receiver.Disconnect(); }
        receivers.Clear();
        foreach (NetChannel channel in channels.Values) { channel.LastPayloadBytes = 0; }
        NetMultiplexer.Release();
        NetRpc.Release();
        NetPing.Release();
    }

    readonly struct CallbackPacket(Action<IWriteMessage> callback) : INetPacket
    {
        public int SizeHintBytes => 0;
        public void Write(IWriteMessage output) => callback?.Invoke(output);
    }

    public readonly struct Frame(byte[] buffer, int length) : INetPacket
    {
        public int SizeHintBytes => length + 3;
        public void Write(IWriteMessage output) => NativeWire.Write(output, buffer, length);
    }

    public readonly unsafe struct BlitPacket<T>(T value) : INetPacket where T : unmanaged
    {
        public int SizeHintBytes => sizeof(T);
        public void Write(IWriteMessage output)
        {
            fixed (T* ptr = &value)
            {
                byte* b = (byte*)ptr;
                for (int i = 0; i < sizeof(T); i++)
                {
                    output.WriteByte(b[i]);
                }
            }
        }
    }

    sealed class Receiver : IDisposable
    {
        readonly NetworkingService service;
        readonly NetChannel channel;
        readonly NetMessageReceived handler;
        bool active = true;
#if CLIENT
        Action<IReadMessage> callback;
        public Receiver(NetworkingService service, NetChannel channel, Action<IReadMessage> callback)
        {
            this.service = service; this.channel = channel; this.callback = callback; handler = Read;
        }
        internal void Invoke(IReadMessage message) => Read(message);
        void Read(IReadMessage message)
        {
            if (!active || !Live) { return; }
            ReceivedMessages++;
            try { callback(message); }
            catch (Exception error) { Failed(channel, error); }
        }
#else
        Action<IReadMessage, Client> callback;
        readonly RateLimit limit;
        public Receiver(NetworkingService service, NetChannel channel, Action<IReadMessage, Client> callback, double minInterval)
        {
            this.service = service; this.channel = channel; this.callback = callback;
            limit = minInterval > 0 ? new RateLimit(minInterval) : null; handler = Read;
        }
        internal void Invoke(IReadMessage message, Client sender) => Read(message, sender);
        void Read(IReadMessage message, Client sender)
        {
            if (!active || !Live || sender == null) { return; }
            if (limit != null && !limit.Allow(sender)) { RateLimited++; return; }
            ReceivedMessages++;
            try { callback(message, sender); }
            catch (Exception error) { Failed(channel, error); }
        }
#endif
        public void Connect()
        {
            service.netReceives.TryGetValue(channel.Key, out NetMessageReceived previous);
            try
            {
                if (CachedChannels) { service.Receive(channel.Key, handler); }
                else { service.Receive(channel.Id, handler); }
            }
            catch
            {
                Disconnect();
                if (previous != null) { service.netReceives.TryAdd(channel.Key, previous); }
                throw;
            }
        }
        public void Disconnect()
        {
            if (!active) { return; }
            active = false;
            callback = null;
            ((ICollection<KeyValuePair<NetworkingService.NetId, NetMessageReceived>>)service.netReceives).Remove(new(channel.Key, handler));
        }
        public void Dispose()
        {
            Disconnect();
            if (receivers.TryGetValue(channel.Id, out Receiver current) && ReferenceEquals(current, this)) { receivers.Remove(channel.Id); }
        }
    }

#if CLIENT
    internal static void Dispatch(string id, IReadMessage message)
    {
        if (receivers.TryGetValue(id, out Receiver r)) { r.Invoke(message); }
    }
    public static void Receive(string id, Action<IReadMessage> callback) => Listen(id, callback);
    public static IDisposable Listen(string id, Action<IReadMessage> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        NetChannel channel = Channel(id);
        if (Service() is not NetworkingService service) { throw new InvalidOperationException("INet listener ownership requires the native LuaCs NetworkingService"); }
        var receiver = new Receiver(service, channel, callback);
        receiver.Connect();
        if (receivers.TryGetValue(id, out Receiver previous)) { previous.Dispose(); }
        receivers.Add(id, receiver);
        return receiver;
    }
    public static void ReceiveLua(string id, LuaCsAction callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Receive(id, message => callback(message, null));
    }
    public static bool SendEmpty(string id) => Send(Channel(id), new CallbackPacket(null));
    public static bool Send(string id, Action<IWriteMessage> write, DeliveryMethod delivery = DeliveryMethod.Reliable) => Send(Channel(id), new CallbackPacket(write), delivery);
    public static bool SendBlit<T>(string id, in T value, DeliveryMethod delivery = DeliveryMethod.Reliable) where T : unmanaged => Send(Channel(id), new BlitPacket<T>(value), delivery);
    public static bool Send<T>(NetChannel channel, in T packet, DeliveryMethod delivery = DeliveryMethod.Reliable) where T : struct, INetPacket
    {
        ArgumentNullException.ThrowIfNull(channel);
        INetworkingService service = Live && GameMain.Client != null ? Service() : null;
        if (service == null) { return false; }
        try
        {
            IWriteMessage message = Start(service, channel);
            int headerBits = message.LengthBits;
            if (CompactState) { int hint = packet.SizeHintBytes; MessageCapacity.Reserve(message, hint > 0 ? hint : channel.LastPayloadBytes); }
            packet.Write(message);
            service.SendToServer(message, delivery);
            SentMessages++; SentBits += message.LengthBits;
            channel.LastPayloadBytes = (message.LengthBits - headerBits + 7) / 8;
            return true;
        }
        catch (Exception error) { Failed(channel, error); return false; }
    }
#else
    internal static void Dispatch(string id, IReadMessage message, Client sender)
    {
        if (receivers.TryGetValue(id, out Receiver r)) { r.Invoke(message, sender); }
    }
    public static void Receive(string id, Action<IReadMessage, Client> callback, double minInterval = 0) => Listen(id, callback, minInterval);
    public static IDisposable Listen(string id, Action<IReadMessage, Client> callback, double minInterval = 0)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!double.IsFinite(minInterval) || minInterval < 0) { throw new ArgumentOutOfRangeException(nameof(minInterval)); }
        NetChannel channel = Channel(id);
        if (Service() is not NetworkingService service) { throw new InvalidOperationException("INet listener ownership requires the native LuaCs NetworkingService"); }
        var receiver = new Receiver(service, channel, callback, minInterval);
        receiver.Connect();
        if (receivers.TryGetValue(id, out Receiver previous)) { previous.Dispose(); }
        receivers.Add(id, receiver);
        return receiver;
    }
    public static void ReceiveLua(string id, LuaCsAction callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Receive(id, (message, client) => callback(message, client));
    }
    public static bool Broadcast(string id, Action<IWriteMessage> write, DeliveryMethod delivery = DeliveryMethod.Reliable, NetAudience audience = NetAudience.All) => Broadcast(Channel(id), new CallbackPacket(write), delivery, audience);
    public static bool BroadcastBlit<T>(string id, in T value, DeliveryMethod delivery = DeliveryMethod.Reliable, NetAudience audience = NetAudience.All) where T : unmanaged => Broadcast(Channel(id), new BlitPacket<T>(value), delivery, audience);
    public static bool Broadcast<T>(NetChannel channel, in T packet, DeliveryMethod delivery = DeliveryMethod.Reliable, NetAudience audience = NetAudience.All) where T : struct, INetPacket => Transmit(channel, null, packet, delivery, audience);
    public static bool Send(string id, NetworkConnection connection, Action<IWriteMessage> write, DeliveryMethod delivery = DeliveryMethod.Reliable) => Send(Channel(id), connection, new CallbackPacket(write), delivery);
    public static bool SendBlit<T>(string id, NetworkConnection connection, in T value, DeliveryMethod delivery = DeliveryMethod.Reliable) where T : unmanaged => Send(Channel(id), connection, new BlitPacket<T>(value), delivery);
    public static bool Send<T>(NetChannel channel, NetworkConnection connection, in T packet, DeliveryMethod delivery = DeliveryMethod.Reliable) where T : struct, INetPacket => connection != null && Transmit(channel, connection, packet, delivery);
    static bool Transmit<T>(NetChannel channel, NetworkConnection connection, in T packet, DeliveryMethod delivery, NetAudience audience = NetAudience.All) where T : struct, INetPacket
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (audience is not (NetAudience.All or NetAudience.InGame)) { throw new ArgumentOutOfRangeException(nameof(audience)); }
        GameServer server = Live ? GameMain.Server : null;
        INetworkingService service = server != null ? Service() : null;
        if (service == null) { return false; }
        IReadOnlyList<Client> targets = audience == NetAudience.InGame ? server.ConnectedClients : null;
        int first = 0;
        if (targets != null)
        {
            while (first < targets.Count && (!targets[first].InGame || targets[first].Connection == null)) { first++; }
            if (first == targets.Count) { return false; }
        }
        try
        {
            IWriteMessage message = Start(service, channel);
            int headerBits = message.LengthBits;
            if (CompactState) { int hint = packet.SizeHintBytes; MessageCapacity.Reserve(message, hint > 0 ? hint : channel.LastPayloadBytes); }
            packet.Write(message);
            if (targets == null) { service.SendToClient(message, connection, delivery); }
            else
            {
                for (int i = first; i < targets.Count; i++)
                {
                    Client target = targets[i];
                    if (target.InGame && target.Connection != null) { service.SendToClient(message, target.Connection, delivery); }
                }
            }
            SentMessages++; SentBits += message.LengthBits;
            channel.LastPayloadBytes = (message.LengthBits - headerBits + 7) / 8;
            return true;
        }
        catch (Exception error) { Failed(channel, error); return false; }
    }
#endif
    static IWriteMessage Start(INetworkingService service, NetChannel channel) => CachedChannels && service is NetworkingService native ? native.Start(channel.Key) : service.Start(channel.Id);
    static INetworkingService Service() => LuaCsSetup.Instance?.Networking;
    static void Failed(NetChannel channel, Exception error)
    {
        Failures++;
        ModLog.Once($"INet {channel.Id}: {error.GetType().Name}: {error.Message}");
    }
    public static string Report() => $"channels={channels.Count} listeners={receivers.Count} sent={SentMessages} bits={SentBits} received={ReceivedMessages} failures={Failures} limited={RateLimited} compact={CompactState} cached={CachedChannels}";
}
