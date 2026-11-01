using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Sounds;

namespace AudioDevices;

public static class AudioDevicePatches {
    public static void TickPostfix() {
        DeviceGate.Beat();
        AudioDeviceCoordinator.Tick();
    }

    public static bool InitializeAlcDevicePrefix(string deviceName, ref bool __result) {
        if (!DeviceGate.Live || AudioDeviceCoordinator.OutputFaulted) {
            if (AudioDeviceCoordinator.MayOpenOutput(GameMain.SoundManager, deviceName)) { return true; }
            __result = false;
            return false;
        }
        if (DeviceGate.Authorized(DeviceOp.OpenOutput)) { return true; }
        AudioDeviceCoordinator.RequestOutput(deviceName);
        __result = false;
        return false;
    }

    public static void InitializeAlcDevicePostfix(SoundManager __instance, bool __result) {
        if (!__result || !DeviceGate.Live) { return; }
        AudioDeviceCoordinator.Adopt(__instance);
    }

    public static bool RefreshOutputPrefix() {
        if (!DeviceGate.Live || AudioDeviceCoordinator.OutputFaulted) { return true; }
        AudioDeviceCoordinator.NotifyOutputLost();
        return false;
    }

    public static bool RefreshCapturePrefix() {
        if (!DeviceGate.Live) { return true; }
        AudioDeviceCoordinator.NotifyCaptureLost();
        return false;
    }

    public static bool CreateCapturePrefix(string deviceName, ref ushort? storedBufferID) {
        if (DeviceGate.Live && !DeviceGate.Authorized(DeviceOp.OpenCapture)) {
            AudioDeviceCoordinator.RequestCapture(deviceName);
            return false;
        }
        storedBufferID = VoiceSequence.Session.Resume(storedBufferID);
        return true;
    }

    public static bool ChangeCaptureDevicePrefix(string deviceName) {
        if (!DeviceGate.Live) { return true; }
        AudioDeviceCoordinator.RequestCapture(deviceName);
        return false;
    }

    public static bool DisposeCapturePrefix(VoipCapture __instance) {
        if (DeviceGate.Live && !DeviceGate.Authorized(DeviceOp.CloseCapture)) {
            AudioDeviceCoordinator.RequestCaptureOff();
            return false;
        }
        VoiceSequence.Session.Retire(__instance.LatestBufferID);
        return true;
    }
}
