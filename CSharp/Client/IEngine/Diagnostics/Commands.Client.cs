namespace IEngine.Diagnostics;

static partial class Commands
{
    static partial void RegisterSide()
    {
        Add("ie_shot", "ie_shot [feature=render] [tag] [twin]: compare frames with the feature off/on (frozen simulation, or twin: one frame rendered off/off/on/on).", args =>
            Say(FrameShot.Start(args.Length > 0 ? args[0] : "render", args.Length > 1 ? args[1] : "shot", args.Length > 2 && args[2] == "twin")));
        Add("ie_cam", "ie_cam [zoom|off]: force the game camera zoom (bench scenarios).", args => Say(CameraOverride.Command(args)));
    }
}
