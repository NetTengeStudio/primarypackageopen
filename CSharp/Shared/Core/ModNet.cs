using System;
using System.Collections.Generic;
using Barotrauma.Networking;

namespace PrimaryPackage.Core;

public static class ModNet {
    private static bool live;
    private static readonly Dictionary<string, Receiver> receivers = new(StringComparer.Ordinal);

    public static bool Live => live;

    public static void Mount() => live = true;

    public static void Release() {
        live = false;
        foreach (var receiver in receivers.Values) { receiver.Disconnect(); }
        receivers.Clear();
    }

    private sealed class Receiver : IDisposable {
        private readonly NetworkingService service;
        private readonly NetworkingService.NetId key;
        private readonly NetMessageReceived handler;
        private readonly string id;
        private bool active = true;
#if CLIENT
        private Action<IReadMessage> callback;
        public Receiver(NetworkingService service, string id, Action<IReadMessage> callback) {
            this.service = service; this.id = id; key = new NetworkingService.NetId(id); this.callback = callback; handler = Read;
        }
        private void Read(IReadMessage message) {
            if (!active || !live) { return; }
            try { callback?.Invoke(message); }
            catch (Exception error) { ModLog.Once($"{id} cannot be read: {error.Message}"); }
        }
#else
        private Action<IReadMessage, Client> callback;
        private readonly RateLimit limit;
        public Receiver(NetworkingService service, string id, Action<IReadMessage, Client> callback, double minInterval) {
            this.service = service; this.id = id; key = new NetworkingService.NetId(id); this.callback = callback;
            limit = minInterval > 0 ? new RateLimit(minInterval) : null; handler = Read;
        }
        private void Read(IReadMessage message, Client sender) {
            if (!active || !live || limit != null && !limit.Allow(sender)) { return; }
            try { callback?.Invoke(message, sender); }
            catch (Exception error) { ModLog.Once($"{id} cannot be read: {error.Message}"); }
        }
#endif
        public void Connect() => service.Receive(id, handler);
        public void Disconnect() {
            if (!active) { return; }
            active = false;
            callback = null;
            ((ICollection<KeyValuePair<NetworkingService.NetId, NetMessageReceived>>)service.netReceives).Remove(new(key, handler));
        }
        public void Dispose() {
            Disconnect();
            if (receivers.TryGetValue(id, out var current) && ReferenceEquals(current, this)) { receivers.Remove(id); }
        }
    }

#if CLIENT
    public static void Receive(string id, Action<IReadMessage> onMessage) {
        Listen(id, onMessage);
    }
    public static IDisposable Listen(string id, Action<IReadMessage> onMessage) {
        if (Service() is not NetworkingService networking || onMessage == null) { return null; }
        if (receivers.TryGetValue(id, out var previous)) { previous.Dispose(); }
        var receiver = new Receiver(networking, id, onMessage);
        receivers.Add(id, receiver);
        receiver.Connect();
        return receiver;
    }

    public static bool Send(string id, Action<IWriteMessage> write,
        DeliveryMethod delivery = DeliveryMethod.Reliable) {
        INetworkingService networking = Sender();
        if (networking == null || GameMain.Client == null) { return false; }
        try {
            IWriteMessage message = networking.Start(id);
            write?.Invoke(message);
            networking.SendToServer(message, delivery);
            return true;
        } catch (Exception error) {
            ModLog.Once($"{id} cannot be sent: {error.Message}");
            return false;
        }
    }
#else
    public static void Receive(string id, Action<IReadMessage, Client> onMessage) =>
        Receive(id, onMessage, 0.0);

    public static void Receive(string id, Action<IReadMessage, Client> onMessage, double minInterval) {
        Listen(id, onMessage, minInterval);
    }
    public static IDisposable Listen(string id, Action<IReadMessage, Client> onMessage, double minInterval = 0) {
        if (Service() is not NetworkingService networking || onMessage == null) { return null; }
        if (receivers.TryGetValue(id, out var previous)) { previous.Dispose(); }
        var receiver = new Receiver(networking, id, onMessage, minInterval);
        receivers.Add(id, receiver);
        receiver.Connect();
        return receiver;
    }

    public static void Broadcast(string id, Action<IWriteMessage> write,
        DeliveryMethod delivery = DeliveryMethod.Reliable) {
        INetworkingService networking = Sender();
        if (networking == null) { return; }
        try {
            IWriteMessage message = networking.Start(id);
            write?.Invoke(message);
            networking.SendToClient(message, null, delivery);
        } catch (Exception error) {
            ModLog.Once($"{id} cannot be sent: {error.Message}");
        }
    }

    public static bool Send(string id, NetworkConnection connection, Action<IWriteMessage> write,
        DeliveryMethod delivery = DeliveryMethod.Reliable) {
        if (connection == null) { return false; }
        INetworkingService networking = Sender();
        if (networking == null) { return false; }
        try {
            IWriteMessage message = networking.Start(id);
            write?.Invoke(message);
            networking.SendToClient(message, connection, delivery);
            return true;
        } catch (Exception error) {
            ModLog.Once($"{id} cannot be sent: {error.Message}");
            return false;
        }
    }
#endif

    private static INetworkingService Sender() => live ? Service() : null;

    private static INetworkingService Service() {
        INetworkingService networking = LuaCsSetup.Instance?.Networking;
        if (networking == null) { ModLog.Once("networking service is missing"); }
        return networking;
    }
}
