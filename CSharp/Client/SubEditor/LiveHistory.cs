using System.Linq;
using Barotrauma;

namespace PrimaryPackage.SubEditor;

/// <summary>A history row of the original editor's panel. The actions themselves live in the server; the row only names them.</summary>
internal sealed class LiveCommand : Command
{
    public readonly uint Request;
    private readonly string text;

    public LiveCommand(uint request, string text) { Request = request; this.text = text; }

    public override LocalizedString GetDescription() => new RawLString(text);
    public override void Execute() { }
    public override void UnExecute() { }
    public override void Cleanup() { }
}

/// <summary>
/// Keeps the original editor's history panel and its undo/redo pointer in step with the server's per-editor history,
/// so the panel, its tooltips and its click-to-jump work unchanged. A row appears when a request is sent, disappears if the
/// server refuses it, and the pointer moves only when the server confirms an undo or redo.
/// </summary>
internal static class LiveHistory
{
    public static string Describe(OpKind kind) => kind switch
    {
        OpKind.Spawn => "Place",
        OpKind.Delete => "Delete",
        OpKind.Transform => "Move or resize",
        OpKind.Property => "Edit properties",
        OpKind.Link => "Link",
        OpKind.Wire or OpKind.WireNew => "Wiring",
        OpKind.Inventory => "Move item",
        OpKind.SubMeta => "Submarine settings",
        OpKind.Waypoints => "Generate waypoints",
        OpKind.Flip => "Mirror",
        OpKind.SpawnSub => "Add submarine",
        OpKind.DeleteSub => "Remove submarine",
        OpKind.State => "Transform",
        _ => kind.ToString(),
    };

    public static void Clear()
    {
        SubEditorScreen.Commands.Clear();
        SubEditorScreen.commandIndex = 0;
        Refresh();
    }

    public static void Push(uint request, string text)
    {
        var commands = SubEditorScreen.Commands;
        if (SubEditorScreen.commandIndex < commands.Count) { commands.RemoveRange(SubEditorScreen.commandIndex, commands.Count - SubEditorScreen.commandIndex); }
        commands.Add(new LiveCommand(request, text));
        int limit = System.Math.Clamp(GameSettings.CurrentConfig.SubEditorUndoBuffer, 1, 10240);
        while (commands.Count > limit) { commands.RemoveAt(0); }
        SubEditorScreen.commandIndex = commands.Count;
        Refresh();
    }

    public static void Rejected(uint request)
    {
        var commands = SubEditorScreen.Commands;
        int at = commands.FindIndex(c => c is LiveCommand l && l.Request == request);
        if (at < 0) { return; }
        commands.RemoveAt(at);
        if (at < SubEditorScreen.commandIndex) { SubEditorScreen.commandIndex--; }
        Refresh();
    }

    /// <summary>The server undid (or redid) the newest entry of this editor's history.</summary>
    public static void Moved(bool undo)
    {
        if (undo && SubEditorScreen.commandIndex > 0) { SubEditorScreen.commandIndex--; }
        else if (!undo && SubEditorScreen.commandIndex < SubEditorScreen.Commands.Count) { SubEditorScreen.commandIndex++; }
        Refresh();
    }

    public static void Refresh() => GameMain.SubEditorScreen?.UpdateUndoHistoryPanel();
}
