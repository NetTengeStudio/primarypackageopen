using PrimaryPackage.Core;

namespace PrimaryPackage;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif

public sealed class PrimaryPackagePlugin : IAssemblyPlugin {
    private const string HarmonyId = "com.primarypackage";

    public ILoggerService LoggerService { get; set; }

    public IPluginManagementService PluginManagementService { get; set; }

    public IConfigService ConfigService { get; set; }

    public void PreInitPatching() {
        ModLog.Mount(LoggerService);
        if (!ModAssets.Mount<PrimaryPackagePlugin>(PluginManagementService)) {
            ModLog.Error("mod root cannot be resolved");
        }
        ModConfig.Mount(ConfigService);
#if CLIENT
        ModSettings.Bind();
#endif
        INet.Net.Mount();
    }

    public void Initialize() {
        ModCompiler.Initialize();
        ModuleHost.Attach(HarmonyId, Modules());
    }

    public void OnLoadCompleted() {
        ModuleHost.LoadCompleted();
        ModCompiler.ReportStartup();
    }

    public void Dispose() {
        ModuleHost.Release();
        INet.Net.Release();
        ModCommands.Release();
#if CLIENT
        ModSettings.Release();
        ShaderStore.Release();
        SoundStore.Release();
        TextureStore.Release();
#endif
        ModConfig.Release();
        ModAssets.Release();
        ModLog.Release();
    }

    private static IModule[] Modules() {
#if CLIENT
        return new IModule[] {
            new ConsolePresentationPlugin(),
            new PrimaryPackage.GUI.GUIBridge(),
            new PrimaryPackage.Interactions.InteractionClient(),
            new PrimaryPackage.SubEditor.SubEditorClient(),
            new TextMotion.TextMotionPlugin(),
            new BandSlot.BandSlotPlugin(),
            new Cold.ColdPlugin(),
            new CrewManifest.CrewManifestPlugin(),
            new Economy.EconomyRoundResetPlugin(),
            new INet.ContentParityPlugin(),
            new INet.FragmentReceiver(),
            new INet.WireIndexPlugin(),
            new INet.NetSyncAcceleratorPlugin(),
            new Fault.FaultContainmentPlugin(),
            new Inspect.InspectRegistryPlugin(),
            new SlotLayout.SlotLayoutPlugin(),
            new Economy.OrderSetsPlugin(),
            new Spawn.SpawnPlugin(),
            new Doors.HatchStandingPlugin(),
            new Commands.WireDeletePlugin(),
            new Aes.AesPlugin(),
            new AudioDevices.AudioDevicePlugin(),
            new AudioDevices.SoundPlayerCrashFixPlugin(),
            new Ambient.AmbientClientPlugin(),
            new Economy.AnnouncementSoundPlugin(),
            new BackHolster.BackHolsterPlugin(),
            new Bloom.BloomPlugin(),
            new MotionBlur.MotionBlurPlugin(),
            new Economy.CheckPrintInteractionClientPlugin(),
            new CoalitionDocument.CoalitionDocumentPlugin(),
            new Cold.ColdClientPlugin(),
            new INet.CsActiveClientPlugin(),
            new Bloom.ExplosionFlashPlugin(),
            new INet.FileTransferGuardPlugin(),
            new Zones.FogCloudRenderPlugin(),
            new Fonts.FontRegistryPlugin(),
            new Economy.HintToastClientPlugin(),
            new Inspect.InspectEditorPlugin(),
            new Inspect.InspectPromptPlugin(),
            new Fixes.LevelResourceFixPlugin(),
            new Fixes.SteamLobbyGuardPlugin(),
            new ModInventory.ModInventoryPlugin(),
            new MoreBlood.Mod(),
            new Economy.OrderCrateBakerPlugin(),
            new RPWheel.RPWheelMod(),
            new Zones.ScriptToastPlugin(),
            new SoundPolyphony.SoundPolyphonyPlugin(),
            new Spawn.SpawnClientPlugin(),
            new Documents.StampInteractionClientPlugin(),
            new Thoughts.ThoughtsPlugin(),
            new Wake.WakeClientPlugin(),
            new Reload.WeaponReloadPlugin(),
            new WeaponSwap.WeaponSwapPlugin(),
            new ZoomKey.ZoomKeyPlugin(),
            new INet.ChildServerRelayPatchPlugin(),
            new PrimaryPackage.Combat.DamageGuardPlugin(),
            new AudioDevices.SoundManagerGainPatchPlugin(),
            new WallCollision.WallCollisionPlugin(),
            new IEngine.EngineModule()
        };
#else
        return new IModule[] {
            new PrimaryPackage.Interactions.InteractionServer(),
            new PrimaryPackage.SubEditor.SubEditorServer(),
            new BandSlot.BandSlotPlugin(),
            new Cold.ColdPlugin(),
            new CrewManifest.CrewManifestPlugin(),
            new Economy.EconomyRoundResetPlugin(),
            new INet.ContentParityPlugin(),
            new INet.FragmentReceiver(),
            new INet.WireIndexPlugin(),
            new INet.NetSyncAcceleratorPlugin(),
            new Fault.FaultContainmentPlugin(),
            new Inspect.InspectRegistryPlugin(),
            new SlotLayout.SlotLayoutPlugin(),
            new Economy.OrderSetsPlugin(),
            new Spawn.SpawnPlugin(),
            new Commands.WireDeletePlugin(),
            new Ambient.AmbientServerPlugin(),
            new Economy.CheckPrintInteractionServerPlugin(),
            new Doors.HatchStandingPlugin(),
            new Cold.ColdServerPlugin(),
            new INet.CsGuardPlugin(),
            new Economy.EconomyPlugin(),
            new Economy.OrderBoardPlugin(),
            new RPWheel.RPWheelServer(),
            new Documents.StampInteractionServerPlugin(),
            new Commands.ConsoleCommandInterceptPlugin(),
            new PrimaryPackage.Server.Safety.EventManagerSafetyPlugin(),
            new PrimaryPackage.Server.Safety.CrewManagerSafetyPlugin(),
            new INet.ChildServerRelayPatchPlugin(),
            new PrimaryPackage.Combat.DamageGuardPlugin(),
            new WallCollision.WallCollisionPlugin(),
            new IEngine.EngineModule()
        };
#endif
    }
}
