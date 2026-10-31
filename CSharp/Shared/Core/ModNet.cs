using System;
using Barotrauma.Networking;

namespace PrimaryPackage.Core;

public static class ModNet {
    private static bool live;

    public static bool Live => live;

    public static void Mount() => live = true;

    public static void Release() => live = false;

#if CLIENT
    public static void Receive(string id, Action<IReadMessage> onMessage) {
        INetworkingService networking = Service();
        if (networking == null || onMessage == null) { return; }
        networking.Receive(id, (NetMessageReceived)(message => {
            if (!live) { return; }
            try {
                onMessage(message);
            } catch (Exception error) {
                ModLog.Once($"{id} cannot be read: {error.Message}");
            }
        }));
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
        INetworkingService networking = Service();
        if (networking == null || onMessage == null) { return; }
        RateLimit limit = minInterval > 0.0 ? new RateLimit(minInterval) : null;
        networking.Receive(id, (NetMessageReceived)((message, sender) => {
            if (!live) { return; }
            if (limit != null && !limit.Allow(sender)) { return; }
            try {
                onMessage(message, sender);
            } catch (Exception error) {
                ModLog.Once($"{id} cannot be read: {error.Message}");
            }
        }));
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
