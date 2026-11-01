using System;

namespace AudioDevices;

public struct DeviceChannel {
    public const int Unknown = 0;

    public const int Present = 1;

    public const int Absent = 2;

    public const int Returned = 3;

    public const int FirstMs = 1000;

    public const int MaxMs = 10000;

    public const int Confirm = 2;

    public string Applied;

    public string Preferred;

    public string Asked;

    public string Shown;

    public bool Faulted;

    public int Verdict;

    public int Strikes;

    public long HoldUntil;

    public int Delay;

    public void Rearm() {
        Faulted = false;
        Asked = null;
        Verdict = Unknown;
        Strikes = 0;
        HoldUntil = 0;
        Delay = FirstMs;
    }

    public void Won() {
        Verdict = Unknown;
        HoldUntil = 0;
        Delay = FirstMs;
    }

    public void Chosen() {
        HoldUntil = 0;
        Delay = FirstMs;
    }

    public void Failed(long now) {
        if (Delay <= 0) { Delay = FirstMs; }
        HoldUntil = now + Delay;
        Delay = Math.Min(Delay << 1, MaxMs);
    }

    public void Defer(long now, int span) => HoldUntil = now + span;

    public bool Ready(long now) => HoldUntil <= now;

    public bool Restless(long now) =>
        !Faulted && ((Applied != null && Verdict != Present) || HoldUntil > now);

    public long Remain(long now) => HoldUntil <= now ? 0 : HoldUntil - now;

    public bool Judge(int mask, bool differs, out int verdict) {
        verdict = (mask & DeviceRoster.HasFirst) == 0 ? Absent
            : (mask & DeviceRoster.HasSecond) != 0 && differs ? Returned
            : Present;
        if (verdict == Absent) {
            if (Strikes < Confirm) { Strikes++; }
            if (Strikes < Confirm) { verdict = Verdict; return false; }
        } else {
            Strikes = 0;
        }
        if (verdict == Verdict) { return false; }
        Verdict = verdict;
        return true;
    }
}
