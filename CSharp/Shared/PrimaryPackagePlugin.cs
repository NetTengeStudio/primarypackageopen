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
        ModNet.Mount();
    }

    public void Initialize() => ModuleHost.Attach(HarmonyId, Modules());

    public void OnLoadCompleted() => ModuleHost.LoadCompleted();

    public void Dispose() {
        ModuleHost.Release();
        ModNet.Release();
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
            new TextMotion.TextMotionPlugin(),
            new BandSlot.BandSlotPlugin(),
            new Cold.ColdPlugin(),
            new CrewManifest.CrewManifestPlugin(),
            new Economy.EconomyRoundResetPlugin(),
            new Netcode.EntityIdGuardPlugin(),
            new Netcode.ContentParityPlugin(),
            new Netcode.WireIndexPlugin(),
            new Fault.FaultContainmentPlugin(),
            new Netcode.NetFaultGuardPlugin(),
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
            new Netcode.CsActiveClientPlugin(),
            new Bloom.ExplosionFlashPlugin(),
            new Netcode.FileTransferGuardPlugin(),
            new Zones.FogCloudRenderPlugin(),
            new Fonts.FontRegistryPlugin(),
            new Economy.HintToastClientPlugin(),
            new Inspect.InspectEditorPlugin(),
            new Inspect.InspectPromptPlugin(),
            new Kits.KitStudioPlugin(),
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
            new PrimaryPackage.Networking.ChildServerRelayPatchPlugin(),
            new PrimaryPackage.Combat.DamageGuardPlugin(),
            new AudioDevices.SoundManagerGainPatchPlugin(),
            new WallCollision.WallCollisionPlugin(),
            new IEngine.EngineModule()
        };
#else
        return new IModule[] {
            new PrimaryPackage.Interactions.InteractionServer(),
            new BandSlot.BandSlotPlugin(),
            new Cold.ColdPlugin(),
            new CrewManifest.CrewManifestPlugin(),
            new Economy.EconomyRoundResetPlugin(),
            new Netcode.EntityIdGuardPlugin(),
            new Netcode.ContentParityPlugin(),
            new Netcode.WireIndexPlugin(),
            new Fault.FaultContainmentPlugin(),
            new Netcode.NetFaultGuardPlugin(),
            new Inspect.InspectRegistryPlugin(),
            new SlotLayout.SlotLayoutPlugin(),
            new Economy.OrderSetsPlugin(),
            new Spawn.SpawnPlugin(),
            new Commands.WireDeletePlugin(),
            new Ambient.AmbientServerPlugin(),
            new Economy.CheckPrintInteractionServerPlugin(),
            new Doors.HatchStandingPlugin(),
            new Cold.ColdServerPlugin(),
            new Netcode.CsGuardPlugin(),
            new Economy.EconomyPlugin(),
            new Kits.KitServerPlugin(),
            new Economy.OrderBoardPlugin(),
            new RPWheel.RPWheelServer(),
            new Documents.StampInteractionServerPlugin(),
            new Netcode.SyncRecoveryPlugin(),
            new Commands.ConsoleCommandInterceptPlugin(),
            new PrimaryPackage.Server.Safety.EventManagerSafetyPlugin(),
            new PrimaryPackage.Server.Safety.CrewManagerSafetyPlugin(),
            new PrimaryPackage.Networking.ChildServerRelayPatchPlugin(),
            new PrimaryPackage.Combat.DamageGuardPlugin(),
            new WallCollision.WallCollisionPlugin(),
            new IEngine.EngineModule()
        };
#endif
    }
}
