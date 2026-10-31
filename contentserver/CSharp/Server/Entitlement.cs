using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;

namespace SubSeal.Server
{
    internal enum Claim : byte
    {
        None,
        Active,
        Public,
        CampaignOffered,
        Browsed,
        Protected
    }

    internal sealed class Entitlement
    {
        private readonly Settings settings;
        private readonly Budget budget;

        internal Entitlement(Settings settings, Budget budget)
        {
            this.settings = settings;
            this.budget = budget;
        }

        internal bool Allows(NetworkConnection connection, FileTransferType fileType, string filePath, out Claim claim)
        {
            claim = Classify(fileType, filePath);

            switch (claim)
            {
                case Claim.Active:
                case Claim.Public:
                case Claim.CampaignOffered:
                    return true;
                case Claim.Browsed:
                    return budget.TryTake(Identity.Of(connection), filePath);
                default:
                    return false;
            }
        }

        private Claim Classify(FileTransferType fileType, string filePath)
        {
            if (fileType == FileTransferType.CampaignSave)
            {
                return GameMain.GameSession?.Campaign is not null ? Claim.Active : Claim.None;
            }

            if (fileType != FileTransferType.Submarine) { return Claim.None; }

            if (IsActive(filePath)) { return Claim.Active; }

            if (settings.PreviewProtection)
            {
                return settings.IsPublicSubmarine(filePath) ? Claim.Public : Claim.Protected;
            }

            if (settings.RestrictToActiveSubmarines) { return Claim.None; }

            if (IsCampaignOffered(filePath)) { return Claim.CampaignOffered; }

            if (GameMain.Server.GameStarted) { return Claim.None; }

            return Claim.Browsed;
        }

        private static bool IsActive(string filePath)
        {
            NetLobbyScreen lobby = GameMain.NetLobbyScreen;
            if (Matches(lobby?.SelectedSub, filePath)) { return true; }
            if (Matches(lobby?.SelectedShuttle, filePath)) { return true; }
            if (Matches(lobby?.SelectedEnemySub, filePath)) { return true; }

            GameSession session = GameMain.GameSession;
            if (session is null) { return false; }
            if (Matches(session.SubmarineInfo, filePath)) { return true; }

            foreach (SubmarineInfo owned in session.OwnedSubmarines)
            {
                if (Matches(owned, filePath)) { return true; }
            }

            return false;
        }

        private static bool IsCampaignOffered(string filePath)
        {
            NetLobbyScreen lobby = GameMain.NetLobbyScreen;
            if (lobby is null || lobby.SelectedMode != GameModePreset.MultiPlayerCampaign) { return false; }

            IReadOnlyList<SubmarineInfo> offered = lobby.GetSubList();
            if (offered is null) { return false; }

            HashSet<string> hidden = GameMain.Server?.ServerSettings?.HiddenSubs;

            foreach (SubmarineInfo sub in offered)
            {
                if (!Matches(sub, filePath)) { continue; }
                return hidden is null || !hidden.Contains(sub.Name);
            }

            return false;
        }

        private static bool Matches(SubmarineInfo info, string filePath)
            => info is not null && string.Equals(info.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
    }
}
