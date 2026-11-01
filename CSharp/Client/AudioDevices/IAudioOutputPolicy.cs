using Barotrauma.Sounds;

namespace AudioDevices;

public interface IAudioOutputPolicy {
    AudioSubsystemState State { get; }

    int Missing { get; }

    void Mount(SoundManager manager);

    bool MayOpen(SoundManager manager, string target);
}
