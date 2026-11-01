namespace AudioDevices;

public static class AudioSubsystem {
    public static AudioSubsystemState Classify(bool hasManager, bool disabled, bool disconnected, int missing) =>
        !hasManager ? AudioSubsystemState.Uninitialized
        : missing > 0 ? AudioSubsystemState.DeviceUnavailable
        : disabled || disconnected ? AudioSubsystemState.DeviceLost
        : AudioSubsystemState.Ready;

    public static bool MayOpen(AudioSubsystemState state) =>
        state == AudioSubsystemState.DeviceLost || state == AudioSubsystemState.Ready;
}
