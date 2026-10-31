namespace AudioDevices;

public sealed class VoiceSequence {
    public static readonly VoiceSequence Session = new VoiceSequence();

    private readonly object gate = new object();

    private bool known;

    private ushort highWater;

    public static bool MoreRecent(ushort next, ushort old) =>
        next > old ? next - old <= 32767 : old > next && old - next > 32767;

    public void Retire(ushort latest) {
        lock (gate) {
            if (!known || MoreRecent(latest, highWater)) {
                highWater = latest;
                known = true;
            }
        }
    }

    public ushort? Resume(ushort? offered) {
        lock (gate) {
            return known ? highWater : offered;
        }
    }
}
