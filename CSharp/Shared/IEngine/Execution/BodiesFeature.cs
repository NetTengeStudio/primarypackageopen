using HarmonyLib;

namespace IEngine.Execution;

sealed class BodiesFeature : Feature
{
    public override string Name => "bodies";

    protected override string Install(Harmony harmony)
    {
        return null;
    }

    protected override void OnToggled()
    {
        if (BodyIndex.On) { BodyIndex.Stop(); }
    }

    internal override void OnLoadCompleted() => OnToggled();

    public override void SetVerify(bool on) { }

    public override string Report() => "native PhysicsBody.List traversal preserved; indexed update disabled until lifecycle parity is verified";
}
