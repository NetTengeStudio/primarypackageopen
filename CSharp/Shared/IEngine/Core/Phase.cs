namespace IEngine.Core;

public enum Phase : byte
{
    Tick,
    MapEntity,
    Hulls,
    Gaps,
    Power,
    Items,
    SpriteStates,
    Characters,
    Animation,
    StatusEffects,
    Ragdolls,
    Physics,
    Network,
    Draw,
    Present,
    Frame,
    Spawning,
    Count
}
