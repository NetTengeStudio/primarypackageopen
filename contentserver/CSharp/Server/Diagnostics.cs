using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;

namespace SubSeal.Server
{
    internal static class Diagnostics
    {
        private const double RepeatIntervalSeconds = 60.0;
        private const int MaxTrackedMessages = 512;

        private static Action<string> note;
        private static Action<string> fault;

        private static readonly Dictionary<string, double> lastSaid = new Dictionary<string, double>(StringComparer.Ordinal);

        internal static void Redirect(Action<string> note, Action<string> fault)
        {
            Diagnostics.note = note;
            Diagnostics.fault = fault;
        }

        internal static void Reset()
        {
            note = null;
            fault = null;
            lastSaid.Clear();
        }

        internal static void Note(string message)
        {
            if (Repeated(message)) { return; }
            if (note is not null) { note(message); return; }
            LuaCsLogger.Log(message);
        }

        private static bool Repeated(string message)
        {
            double now = Timing.TotalTime;

            if (lastSaid.TryGetValue(message, out double previous) && now - previous < RepeatIntervalSeconds)
            {
                return true;
            }

            if (lastSaid.Count >= MaxTrackedMessages) { lastSaid.Clear(); }

            lastSaid[message] = now;
            return false;
        }

        internal static void Fault(string message)
        {
            if (fault is not null) { fault(message); return; }
            LuaCsLogger.LogError(message);
        }

        internal static string Describe(NetworkConnection connection)
        {
            if (connection is null) { return "unknown client"; }

            if (GameMain.Server?.ConnectedClients is not null)
            {
                foreach (Client client in GameMain.Server.ConnectedClients)
                {
                    if (client.Connection == connection) { return client.Name; }
                }
            }

            return connection.Endpoint?.StringRepresentation ?? "unknown client";
        }
    }
}
