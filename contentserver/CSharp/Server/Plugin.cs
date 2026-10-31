using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;

namespace SubSeal.Server
{
    public sealed class Plugin : IAssemblyPlugin, IEventClientDisconnected, IEventUpdate
    {
        private const string HarmonyId = "nettenge.subseal.server";
        private const double SweepIntervalSeconds = 5.0;

        private readonly Harmony harmony = new Harmony(HarmonyId);

        private Settings settings;
        private SessionKeys sessions;
        private Leases leases;
        private Budget budget;
        private Entitlement entitlement;
        private LeaseChannel channel;
        private Commands commands;
        private double nextSweep;
        private bool active;

        public void PreInitPatching() { }

        public void Initialize()
        {
            settings = Settings.Load(FindPackageDirectory());
            Diagnostics.Note(settings.PreviewProtection ? "preview protection enabled" : "preview protection disabled");
            sessions = new SessionKeys();
            leases = new Leases();
            budget = new Budget(settings.BudgetCapacity, settings.BudgetRefillPerSecond, settings.BudgetRetentionSeconds);
            entitlement = new Entitlement(settings, budget);
            channel = new LeaseChannel(sessions, leases, settings.PreviewProtection);
            commands = new Commands(sessions, leases, channel);

            ModTransferPolicy.Build(settings);
            TransferPatches.Bind(settings, sessions, leases, entitlement, channel);

            if (Gate.Install(harmony, settings) == GateMode.None)
            {
                TransferPatches.Unbind();
                ModTransferPolicy.Clear();
                harmony.UnpatchSelf();
                EngineSymbols.Forget();
                Gate.Forget();
                return;
            }

            if (Gate.Mode == GateMode.Sealing) { channel.Listen(); }

            commands.Add();

            LuaCsSetup.Instance.EventService.Subscribe<IEventClientDisconnected>(this);
            LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);

            active = true;
        }

        public void OnLoadCompleted() { }

        public void OnClientDisconnected(Client client)
        {
            if (client?.Connection is null) { return; }
            leases.Revoke(client.Connection);
            sessions.Drop(client.Connection);
            TransferPatches.DropDeferred(client.Connection);

        }

        public void OnUpdate(double fixedDeltaTime)
        {
            if (!active || Timing.TotalTime < nextSweep) { return; }
            nextSweep = Timing.TotalTime + SweepIntervalSeconds;
            TransferPatches.RetryArmed(leases);
            leases.Sweep(settings.LeaseRetentionSeconds);
            budget.Sweep();
        }

        public void Dispose()
        {
            if (active)
            {
                LuaCsSetup.Instance.EventService.Unsubscribe<IEventClientDisconnected>(this);
                LuaCsSetup.Instance.EventService.Unsubscribe<IEventUpdate>(this);
                commands.Remove();
                leases.RevokeAll();
                sessions.DropAll();
                budget.Clear();
                active = false;
            }

            TransferPatches.Unbind();
            ModTransferPolicy.Clear();
            harmony.UnpatchSelf();
            EngineSymbols.Forget();
            Gate.Forget();
        }

        private static string FindPackageDirectory()
        {
            foreach (ContentPackage package in ContentPackageManager.EnabledPackages.Regular)
            {
                if (string.IsNullOrEmpty(package.Dir)) { continue; }
                if (System.IO.File.Exists(System.IO.Path.Combine(package.Dir, "CSharp", "Server", "Gate.cs"))) { return package.Dir; }
            }

            foreach (ContentPackage package in ContentPackageManager.EnabledPackages.Regular)
            {
                if (package.Name.Equals("contentserver", StringComparison.OrdinalIgnoreCase)) { return package.Dir; }
            }

            return null;
        }
    }
}
