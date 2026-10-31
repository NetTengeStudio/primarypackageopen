using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using HarmonyLib;
using SubSeal.Wire;

namespace SubSeal.Client
{
    public sealed class Plugin : IAssemblyPlugin, IEventUpdate
    {
        private const string HarmonyId = "nettenge.subseal.client";
        private const double HelloRetrySeconds = 3.0;

        private readonly Harmony harmony = new Harmony(HarmonyId);

        private SessionKey sessionKey;
        private LeaseStore leases;
        private LeaseChannel channel;
        private Commands commands;
        private byte capabilities;
        private double nextHelloAt;
        private object clientIdentity;
        private bool active;

        public void PreInitPatching() { }

        public void Initialize()
        {
            sessionKey = new SessionKey();
            leases = new LeaseStore();
            channel = new LeaseChannel(sessionKey, leases);
            commands = new Commands();

            commands.Add();
            channel.Listen();

            PreviewLock.Bind(leases);

            capabilities = 0;
            if (EngineSymbols.Resolve() && TryPatch())
            {
                capabilities |= Protocol.CapabilityCanOpenSealed;
            }

            LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);

            active = true;
        }

        public void OnUpdate(double fixedDeltaTime)
        {
            object client = GameMain.Client;
            if (!ReferenceEquals(client, clientIdentity))
            {
                clientIdentity = client;
                nextHelloAt = 0.0;
                sessionKey.Discard();
                leases.ReleaseAll();
                PreviewLock.Clear();
            }

            if (client is not null && !sessionKey.Established && Timing.TotalTime >= nextHelloAt)
            {
                nextHelloAt = Timing.TotalTime + HelloRetrySeconds;
                channel.SendHello(capabilities);
            }

            if (PreviewLock.ConsumeRefresh())
            {
                PreviewLock.RefreshLobby();
            }
        }

        private bool TryPatch()
        {
            try
            {
                OpenPatches.Bind(leases);

                harmony.Patch(
                    EngineSymbols.DecompressFileToStream,
                    new HarmonyMethod(typeof(OpenPatches).GetMethod(nameof(OpenPatches.BeforeDecompressFileToStream))));

                harmony.Patch(
                    EngineSymbols.SubmarineConstructor,
                    new HarmonyMethod(typeof(LoadPatches).GetMethod(nameof(LoadPatches.BeforeSubmarineConstructed))),
                    new HarmonyMethod(typeof(LoadPatches).GetMethod(nameof(LoadPatches.AfterSubmarineConstructed))));

                harmony.Patch(
                    EngineSymbols.PreviewCreate,
                    new HarmonyMethod(typeof(LoadPatches).GetMethod(nameof(LoadPatches.BeforePreviewCreate))),
                    new HarmonyMethod(typeof(LoadPatches).GetMethod(nameof(LoadPatches.AfterPreviewCreate))));

                harmony.Patch(
                    EngineSymbols.PreviewClose,
                    postfix: new HarmonyMethod(typeof(LoadPatches).GetMethod(nameof(LoadPatches.AfterPreviewClose))));
            }
            catch (Exception)
            {
                harmony.UnpatchSelf();
                OpenPatches.Bind(null);
                return false;
            }

            TryPatchPreviewUi();
            TryPatchAdditionalPreviewSurfaces();
            TryPatchCampaignRetry();
            return true;
        }

        private void TryPatchAdditionalPreviewSurfaces()
        {
            // Each surface is independent. A version mismatch must not remove
            // the core SubSeal patches or disrupt GUI navigation.
            TryPatchAdditional(EngineSymbols.TabMenuPreview,
                nameof(PreviewSurfacePatches.BeforeCreateTabPreview),
                nameof(PreviewSurfacePatches.AfterCreateTabPreview));
            TryPatchAdditional(EngineSymbols.SelectionPreviewImage,
                nameof(PreviewSurfacePatches.BeforeGetSelectionPreview), null);
            TryPatchAdditional(EngineSymbols.SelectionRefresh, null,
                nameof(PreviewSurfacePatches.AfterSelectionRefresh));
            TryPatchAdditional(EngineSymbols.SelectionSelect, null,
                nameof(PreviewSurfacePatches.AfterSelectSubmarine));
        }

        private void TryPatchAdditional(System.Reflection.MethodInfo target, string prefix, string postfix)
        {
            if (target is null) { return; }
            try
            {
                harmony.Patch(target,
                    prefix: prefix is null ? null : new HarmonyMethod(
                        typeof(PreviewSurfacePatches).GetMethod(prefix, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)),
                    postfix: postfix is null ? null : new HarmonyMethod(
                        typeof(PreviewSurfacePatches).GetMethod(postfix, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)));
            }
            catch (Exception)
            {
                harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id);
            }
        }

        private void TryPatchCampaignRetry()
        {
            if (EngineSymbols.CampaignRetry is null) { return; }

            try
            {
                harmony.Patch(
                    EngineSymbols.CampaignRetry,
                    prefix: new HarmonyMethod(typeof(PreviewPatches).GetMethod(nameof(PreviewPatches.BeforeCampaignRetry))));
            }
            catch (Exception)
            {
                harmony.Unpatch(EngineSymbols.CampaignRetry, HarmonyPatchType.All, harmony.Id);
            }
        }

        private void TryPatchPreviewUi()
        {
            if (!EngineSymbols.PreviewUiAvailable) { return; }

            try
            {
                harmony.Patch(
                    EngineSymbols.PreviewCard,
                    prefix: new HarmonyMethod(typeof(PreviewPatches).GetMethod(nameof(PreviewPatches.BeforeCreatePreviewWindow))),
                    postfix: new HarmonyMethod(typeof(PreviewPatches).GetMethod(nameof(PreviewPatches.AfterCreatePreviewWindow))));

                harmony.Patch(
                    EngineSymbols.PreviewGenerateMeshes,
                    prefix: new HarmonyMethod(typeof(PreviewPatches).GetMethod(nameof(PreviewPatches.BeforeGeneratePreviewMeshes))));

                harmony.Patch(
                    EngineSymbols.PreviewRender,
                    prefix: new HarmonyMethod(typeof(PreviewPatches).GetMethod(nameof(PreviewPatches.BeforeRenderSubmarine))));

                if (EngineSymbols.PreviewDispose is not null)
                {
                    harmony.Patch(
                        EngineSymbols.PreviewDispose,
                        postfix: new HarmonyMethod(typeof(PreviewPatches).GetMethod(nameof(PreviewPatches.AfterPreviewDisposed))));
                }
            }
            catch (Exception)
            {
                UnpatchPreviewUi();
            }
        }

        private void UnpatchPreviewUi()
        {
            if (EngineSymbols.PreviewCard is not null) { harmony.Unpatch(EngineSymbols.PreviewCard, HarmonyPatchType.All, harmony.Id); }
            if (EngineSymbols.PreviewGenerateMeshes is not null) { harmony.Unpatch(EngineSymbols.PreviewGenerateMeshes, HarmonyPatchType.All, harmony.Id); }
            if (EngineSymbols.PreviewRender is not null) { harmony.Unpatch(EngineSymbols.PreviewRender, HarmonyPatchType.All, harmony.Id); }
            if (EngineSymbols.PreviewDispose is not null) { harmony.Unpatch(EngineSymbols.PreviewDispose, HarmonyPatchType.All, harmony.Id); }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            if (active)
            {
                LuaCsSetup.Instance.EventService.Unsubscribe<IEventUpdate>(this);
                commands?.Remove();
                leases?.ReleaseAll();
                sessionKey?.Discard();
                clientIdentity = null;
                PreviewLock.Clear();
                OpenPatches.Bind(null);
                Artifacts.DiscardAll();
                active = false;
            }

            harmony.UnpatchSelf();
        }
    }
}
