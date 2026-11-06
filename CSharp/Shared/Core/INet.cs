using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;

namespace PrimaryPackage.Core;

public static class INet
{
    public interface INetPacket
    {
        int SizeHintBytes { get; }
        void Write(IWriteMessage output);
    }

    public readonly record struct NetChannel(string Name)
    {
        public static implicit operator string(NetChannel channel) => channel.Name;
        public static implicit operator NetChannel(string name) => new(name);
        public override string ToString() => Name;
    }

    public static class Net
    {
        private sealed class Subscription : IDisposable
        {
            private volatile bool active = true;
            public bool Active => active;
            public void Dispose() => active = false;
        }

        private static readonly ConcurrentDictionary<string, List<Subscription>> ActiveSubs = new();
        private static object cachedService;
        private static MethodInfo startMethod;
        private static MethodInfo sendMethod;
        private static MethodInfo receiveMethod;
        private static bool initialized;

        private static void EnsureInitialized()
        {
            if (initialized) return;
            try
            {
                object svc = LuaCsSetup.Instance?.Networking;
                if (svc == null)
                {
                    try { svc = LuaCsSetup.Instance?.NetworkingService; } catch { }
                }
                if (svc != null)
                {
                    cachedService = svc;
                    Type t = svc.GetType();
                    startMethod = t.GetMethod("Start", new[] { typeof(string) }) ?? t.GetMethod("Start", Type.EmptyTypes);
                    foreach (MethodInfo m in t.GetMethods())
                    {
                        if (m.Name != "Send") continue;
                        ParameterInfo[] p = m.GetParameters();
                        if (p.Length >= 1 && typeof(IWriteMessage).IsAssignableFrom(p[0].ParameterType))
                        {
                            sendMethod = m;
                            break;
                        }
                    }
                    foreach (MethodInfo m in t.GetMethods())
                    {
                        if (m.Name != "Receive") continue;
                        ParameterInfo[] p = m.GetParameters();
                        if (p.Length >= 2 && p[0].ParameterType == typeof(string))
                        {
                            receiveMethod = m;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("INet init error: " + ex.Message);
            }
            finally
            {
                initialized = true;
            }
        }

        public static NetChannel Channel(string name) => new(name);

        public static IDisposable Listen(string channel, Action<IReadMessage> onClient)
        {
            if (string.IsNullOrEmpty(channel) || onClient == null) return new Subscription();
            EnsureInitialized();
            var sub = new Subscription();
            ActiveSubs.GetOrAdd(channel, static _ => new List<Subscription>()).Add(sub);

            if (cachedService != null && receiveMethod != null)
            {
                try
                {
                    ParameterInfo[] pars = receiveMethod.GetParameters();
                    if (pars.Length == 2)
                    {
                        Type delegateType = pars[1].ParameterType;
                        if (delegateType == typeof(Action<IReadMessage, Client>))
                        {
                            Action<IReadMessage, Client> handler = (msg, _) =>
                            {
                                if (sub.Active) onClient(msg);
                            };
                            receiveMethod.Invoke(cachedService, new object[] { channel, handler });
                            return sub;
                        }
                        if (delegateType == typeof(Action<IReadMessage>))
                        {
                            Action<IReadMessage> handler = msg =>
                            {
                                if (sub.Active) onClient(msg);
                            };
                            receiveMethod.Invoke(cachedService, new object[] { channel, handler });
                            return sub;
                        }
                    }
                    Action<IReadMessage, Client> genericHandler = (msg, _) =>
                    {
                        if (sub.Active) onClient(msg);
                    };
                    receiveMethod.Invoke(cachedService, new object[] { channel, genericHandler });
                }
                catch (Exception ex)
                {
                    ModLog.Error($"INet.Listen client error on {channel}: {ex.Message}");
                }
            }
            return sub;
        }

        public static IDisposable Listen(NetChannel channel, Action<IReadMessage> onClient) => Listen(channel.Name, onClient);

        public static IDisposable Listen(string channel, Action<IReadMessage, Client> onServer)
        {
            if (string.IsNullOrEmpty(channel) || onServer == null) return new Subscription();
            EnsureInitialized();
            var sub = new Subscription();
            ActiveSubs.GetOrAdd(channel, static _ => new List<Subscription>()).Add(sub);

            if (cachedService != null && receiveMethod != null)
            {
                try
                {
                    Action<IReadMessage, Client> handler = (msg, sender) =>
                    {
                        if (sub.Active) onServer(msg, sender);
                    };
                    receiveMethod.Invoke(cachedService, new object[] { channel, handler });
                }
                catch (Exception ex)
                {
                    ModLog.Error($"INet.Listen server error on {channel}: {ex.Message}");
                }
            }
            return sub;
        }

        public static IDisposable Listen(NetChannel channel, Action<IReadMessage, Client> onServer) => Listen(channel.Name, onServer);

        public static bool Send(string channel, Action<IWriteMessage> write, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel, null, write, method);

        public static bool Send(NetChannel channel, Action<IWriteMessage> write, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel.Name, null, write, method);

        public static bool Send(string channel, INetPacket packet, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel, null, packet != null ? packet.Write : null, method);

        public static bool Send(NetChannel channel, INetPacket packet, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel.Name, null, packet != null ? packet.Write : null, method);

        public static bool Send(string channel, NetworkConnection connection, Action<IWriteMessage> write, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel, connection, write, method);

        public static bool Send(NetChannel channel, NetworkConnection connection, Action<IWriteMessage> write, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel.Name, connection, write, method);

        public static bool Send(string channel, NetworkConnection connection, INetPacket packet, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel, connection, packet != null ? packet.Write : null, method);

        public static bool Send(NetChannel channel, NetworkConnection connection, INetPacket packet, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel.Name, connection, packet != null ? packet.Write : null, method);

#if SERVER
        public static bool Send(string channel, Client client, Action<IWriteMessage> write, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel, client?.Connection, write, method);

        public static bool Send(NetChannel channel, Client client, Action<IWriteMessage> write, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel.Name, client?.Connection, write, method);

        public static bool Send(string channel, Client client, INetPacket packet, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel, client?.Connection, packet != null ? packet.Write : null, method);

        public static bool Send(NetChannel channel, Client client, INetPacket packet, DeliveryMethod method = DeliveryMethod.Reliable) =>
            Dispatch(channel.Name, client?.Connection, packet != null ? packet.Write : null, method);
#endif

        private static bool Dispatch(string channel, NetworkConnection connection, Action<IWriteMessage> write, DeliveryMethod method)
        {
            if (string.IsNullOrEmpty(channel) || write == null) return false;
            EnsureInitialized();
            try
            {
                if (cachedService != null && startMethod != null && sendMethod != null)
                {
                    IWriteMessage msg;
                    ParameterInfo[] startParams = startMethod.GetParameters();
                    if (startParams.Length == 1 && startParams[0].ParameterType == typeof(string))
                    {
                        msg = (IWriteMessage)startMethod.Invoke(cachedService, new object[] { channel });
                    }
                    else
                    {
                        msg = (IWriteMessage)startMethod.Invoke(cachedService, Array.Empty<object>());
                        msg.WriteString(channel);
                    }
                    write(msg);
                    ParameterInfo[] sendParams = sendMethod.GetParameters();
                    if (sendParams.Length == 3)
                    {
                        sendMethod.Invoke(cachedService, new object[] { msg, connection, method });
                    }
                    else if (sendParams.Length == 2)
                    {
                        if (sendParams[1].ParameterType == typeof(DeliveryMethod))
                        {
                            sendMethod.Invoke(cachedService, new object[] { msg, method });
                        }
                        else
                        {
                            sendMethod.Invoke(cachedService, new object[] { msg, connection });
                        }
                    }
                    else if (sendParams.Length == 1)
                    {
                        sendMethod.Invoke(cachedService, new object[] { msg });
                    }
                    return true;
                }

                var fallbackMsg = new WriteOnlyMessage();
                fallbackMsg.WriteString(channel);
                write(fallbackMsg);

#if SERVER
                if (GameMain.Server != null)
                {
                    if (connection != null)
                    {
                        GameMain.Server.ServerPeer?.Send(fallbackMsg, connection, method);
                    }
                    else
                    {
                        foreach (Client c in GameMain.Server.ConnectedClients)
                        {
                            if (c?.Connection != null)
                            {
                                GameMain.Server.ServerPeer?.Send(fallbackMsg, c.Connection, method);
                            }
                        }
                    }
                    return true;
                }
#endif
#if CLIENT
                if (GameMain.Client != null)
                {
                    GameMain.Client.ClientPeer?.Send(fallbackMsg, method);
                    return true;
                }
#endif
                return false;
            }
            catch (Exception ex)
            {
                ModLog.Throttled("inet_send_err", $"INet.Send failed for {channel}: {ex.Message}");
                return false;
            }
        }
    }
}
