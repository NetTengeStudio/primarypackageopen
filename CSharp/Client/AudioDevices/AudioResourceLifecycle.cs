using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Sounds;
using PrimaryPackage.Core;

namespace AudioDevices;

public sealed class AudioResourceLifecycle : IAudioOutputPolicy {
    private readonly IEnumerable<SoundPrefab> prefabs;

    private SoundManager observed;

    private int missing;

    private bool told;

    public AudioResourceLifecycle(IEnumerable<SoundPrefab> source) => prefabs = source;

    public int Missing => missing;

    public AudioSubsystemState State => AudioSubsystem.Classify(observed != null,
        observed != null && observed.Disabled, observed != null && observed.Disconnected, missing);

    public void Mount(SoundManager manager) {
        observed = manager;
        told = false;
        missing = manager == null ? 0 : CountMissing(prefabs);
    }

    public bool MayOpen(SoundManager manager, string target) {
        if (manager == null) { return false; }
        observed = manager;
        if (missing == 0 && (manager.Disabled || manager.Disconnected)) { missing = CountMissing(prefabs); }
        if (AudioSubsystem.MayOpen(State)) { return true; }
        if (!told) {
            told = true;
            ModLog.Message($"sound output {target ?? "device"} found, sound returns after a game restart");
        }
        return false;
    }

    public static int CountMissing(IEnumerable<SoundPrefab> source) {
        if (source == null) { return 0; }
        int count = 0;
        foreach (SoundPrefab prefab in source) {
            if (prefab != null && prefab.Sound == null) { count++; }
        }
        return count;
    }
}
