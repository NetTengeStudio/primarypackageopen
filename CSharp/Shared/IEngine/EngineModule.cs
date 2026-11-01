using HarmonyLib;
using PrimaryPackage.Core;

namespace IEngine;

public sealed class EngineModule : IModule, ILoadCompleted
{
    public void Attach(Harmony harmony) => EngineHost.Start(ModAssets.Root);
    public void LoadCompleted() => EngineHost.LoadCompleted();
    public void RoundStart() { }
    public void Release() => EngineHost.Stop();
}
