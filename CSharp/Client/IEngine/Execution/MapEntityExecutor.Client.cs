using Barotrauma;

namespace IEngine.Execution;

static partial class MapEntityExecutor
{
    static partial void UpdateCheats(float deltaTime, Camera cam) => Hull.UpdateCheats(deltaTime, cam);
    static partial void UpdateSpriteStates(float deltaTime) => MapEntity.UpdateAllProjSpecific(deltaTime);
    static partial void Counter(string key, long ticks) => GameMain.PerformanceCounter.AddElapsedTicks(key, ticks);
}
