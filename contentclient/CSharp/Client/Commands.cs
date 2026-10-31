using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;

namespace SubSeal.Client
{
    internal sealed class Commands
    {
        private readonly List<DebugConsole.Command> added = new List<DebugConsole.Command>();

        internal void Add()
        {
            if (added.Count != 0) { return; }

            var listCmd = new DebugConsole.Command("subseal_leases", "subseal_leases", OnListLeases);
            added.Add(listCmd);

            var revokeCmd = new DebugConsole.Command("subseal_revoke", "subseal_revoke [session id|name|all]", OnRevoke, GetRevokeValidArgs);
            added.Add(revokeCmd);

            var previewStatus = new DebugConsole.Command(
                "subseal_preview_status", "subseal_preview_status", OnPreviewStatus);
            previewStatus.RelayToServer = false;
            previewStatus.OnClientExecute = OnPreviewStatus;
            added.Add(previewStatus);

            var previewTest = new DebugConsole.Command(
                "subseal_preview_test", "subseal_preview_test", OnPreviewTest);
            previewTest.RelayToServer = false;
            previewTest.OnClientExecute = OnPreviewTest;
            added.Add(previewTest);

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

        private static void OnPreviewStatus(string[] args)
        {
            DebugConsole.NewMessage(
                $"SubSeal preview: hooks={(PreviewLock.PreviewUiReady ? "ready" : "unavailable")}, " +
                $"mask={(PreviewLock.ProtectionActive ? "active" : "inactive")}, " +
                $"serverPolicy={(PreviewLock.ConfirmedProtected ? "protected" : "not confirmed")}");
        }

        private static void OnPreviewTest(string[] args)
        {
            if (GameMain.Client?.ServerSubmarines is null)
            {
                DebugConsole.NewMessage("SubSeal preview: no network submarine list");
                return;
            }

            if (!PreviewLock.PreviewUiReady || !PreviewLock.ProtectionActive)
            {
                DebugConsole.NewMessage("SubSeal preview: protected renderer unavailable");
                return;
            }

            foreach (SubmarineInfo submarine in GameMain.Client.ServerSubmarines)
            {
                if (submarine is null) { continue; }
                SubmarinePreview.Create(submarine);
                return;
            }

            DebugConsole.NewMessage("SubSeal preview: no submarine available");
        }

        private static string[][] GetRevokeValidArgs()
        {
            List<string> options = new List<string> { "all" };
            if (GameMain.Client?.ConnectedClients is not null)
            {
                foreach (Barotrauma.Networking.Client client in GameMain.Client.ConnectedClients)
                {
                    options.Add(client.SessionId.ToString());
                    options.Add(client.Name);
                }
            }
            return new string[][] { options.ToArray() };
        }

        private static void OnListLeases(string[] args)
        {
            if (GameMain.Client is not null)
            {
                GameMain.Client.SendConsoleCommand("subseal_leases");
            }
            else
            {
                DebugConsole.NewMessage("not connected to a server");
            }
        }

        private static void OnRevoke(string[] args)
        {
            if (GameMain.Client is not null)
            {
                string command = args.Length > 0
                    ? $"subseal_revoke {string.Join(" ", args)}"
                    : "subseal_revoke";
                GameMain.Client.SendConsoleCommand(command);
            }
            else
            {
                DebugConsole.NewMessage("not connected to a server");
            }
        }

    }
}
