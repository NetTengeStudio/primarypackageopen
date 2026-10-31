using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using SubSeal.Wire;

namespace SubSeal.Server
{
    internal sealed class Commands
    {
        private readonly List<DebugConsole.Command> added = new List<DebugConsole.Command>();
        private readonly SessionKeys sessions;
        private readonly Leases leases;
        private readonly LeaseChannel channel;

        internal Commands(SessionKeys sessions, Leases leases, LeaseChannel channel)
        {
            this.sessions = sessions;
            this.leases = leases;
            this.channel = channel;
        }

        internal void Add()
        {
            var listCmd = new DebugConsole.Command("subseal_leases", "subseal_leases", args => ListLeases(null, args));
            listCmd.OnClientRequestExecute = (sender, _, args) => ListLeases(sender, args);
            added.Add(listCmd);

            var revokeCmd = new DebugConsole.Command("subseal_revoke", "subseal_revoke [session id|name|all]", args => Revoke(null, args), GetRevokeValidArgs);
            revokeCmd.OnClientRequestExecute = (sender, _, args) => Revoke(sender, args);
            added.Add(revokeCmd);

            DebugConsole.Commands.AddRange(added);
        }

        internal void Remove()
        {
            foreach (DebugConsole.Command command in added)
            {
                DebugConsole.Commands.Remove(command);
            }
            added.Clear();
        }

        private static string[][] GetRevokeValidArgs()
        {
            List<string> options = new List<string> { "all" };
            if (GameMain.Server?.ConnectedClients is not null)
            {
                foreach (Client client in GameMain.Server.ConnectedClients)
                {
                    options.Add(client.SessionId.ToString());
                    options.Add(client.Name);
                }
            }
            return new string[][] { options.ToArray() };
        }

        private static void Respond(Client sender, string message)
        {
            DebugConsole.NewMessage(message);
            if (sender is not null && GameMain.Server is not null)
            {
                GameMain.Server.SendConsoleMessage(message, sender);
            }
        }

        private void ListLeases(Client sender, string[] args)
        {
            Respond(sender, $"mode {Gate.Mode.ToString().ToLowerInvariant()}");

            int count = 0;
            foreach (Leases.Lease lease in leases.All)
            {
                count++;
                string state = lease.Consumed ? "consumed" : lease.Armed ? "armed" : "pending";
                Respond(sender, $"{lease.Id} {Describe(lease.Connection)} {System.IO.Path.GetFileName(lease.FilePath)} {state}");
            }
            Respond(sender, $"{count} leases");
        }

        private void Revoke(Client sender, string[] args)
        {
            if (args.Length < 1)
            {
                Respond(sender, "subseal_revoke [session id|name|all]");
                return;
            }

            if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (GameMain.Server?.ConnectedClients is not null)
                {
                    foreach (Client client in GameMain.Server.ConnectedClients)
                    {
                        RevokeConnection(client.Connection);
                    }
                }
                leases.RevokeAll();
                sessions.DropAll();
                Respond(sender, "all leases revoked");
                return;
            }

            if (!TryResolve(sender, args[0], out Client target)) { return; }

            RevokeConnection(target.Connection);
            leases.Revoke(target.Connection);
            sessions.Drop(target.Connection);
            Respond(sender, $"{target.SessionId} {target.Name} leases revoked");
        }

        private static bool TryResolve(Client sender, string argument, out Client target)
        {
            target = null;

            if (GameMain.Server?.ConnectedClients is null)
            {
                Respond(sender, $"{argument} not connected");
                return false;
            }

            if (byte.TryParse(argument, out byte sessionId))
            {
                foreach (Client client in GameMain.Server.ConnectedClients)
                {
                    if (client.SessionId != sessionId) { continue; }
                    target = client;
                    break;
                }

                if (target?.Connection is null)
                {
                    Respond(sender, $"{sessionId} not connected");
                    return false;
                }

                return true;
            }

            int matches = 0;
            foreach (Client client in GameMain.Server.ConnectedClients)
            {
                if (!client.Name.Equals(argument, StringComparison.OrdinalIgnoreCase)) { continue; }
                matches++;
                target = client;
            }

            if (matches == 0)
            {
                Respond(sender, $"{argument} not connected");
                return false;
            }

            if (matches > 1)
            {
                target = null;
                Respond(sender, $"{argument} is ambiguous, use a session id");
                return false;
            }

            if (target.Connection is null)
            {
                target = null;
                Respond(sender, $"{argument} not connected");
                return false;
            }

            return true;
        }

        private static string Describe(NetworkConnection connection) => Diagnostics.Describe(connection);

        private void RevokeConnection(NetworkConnection connection)
            => channel.SendRevoke(connection, Protocol.AllLeases);
    }
}
