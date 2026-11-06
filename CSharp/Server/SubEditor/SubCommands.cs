using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// The server side of the <c>spawnsub</c> and <c>deletesub</c> console commands. The engine's own <c>spawnsub</c> exists only on clients and
/// refuses to run in a multiplayer session; these replace it with the editor's pipeline, so the result is the same for everyone in the round.
/// A client's command is relayed here by the console (with its cursor position), and the standard console permission decides who may use it.
/// </summary>
internal static class SubCommands
{
    public static void Register()
    {
        DebugConsole.Command spawn = ModCommands.Register("spawnsub",
            "spawnsub [name] [x y] [is thalamus]: Add a saved submarine to the running round for everyone, at the cursor, at the given world position, or next to the main submarine. For a wreck, the last argument (true/false) says whether it keeps its Thalamus AI.",
            args => Report(null, SubEditorServer.SpawnFromConsole(null, null, args)),
            localOnClient: false, relayToServer: true,
            validArgs: () => new[] { SubmarineInfo.SavedSubmarines.Where(i => !i.IsFileCorrupted).Select(i => i.DisplayName.Value).ToArray() });
        if (spawn != null) { spawn.OnClientRequestExecute = (client, cursor, args) => Report(client, SubEditorServer.SpawnFromConsole(client, cursor, args)); }

        DebugConsole.Command delete = ModCommands.Register("deletesub",
            "deletesub [name|id]: Remove a submarine added by spawnsub or the live editor, for everyone. Without an argument lists them.",
            args => Report(null, SubEditorServer.DeleteFromConsole(null, args)),
            localOnClient: false, relayToServer: true,
            validArgs: () => new[] { SubRegistry.Spawned.Select(s => s.Name).Distinct().ToArray() });
        if (delete != null) { delete.OnClientRequestExecute = (client, cursor, args) => Report(client, SubEditorServer.DeleteFromConsole(client, args)); }
    }

    private static void Report(Client client, (bool Ok, string Text) result)
    {
        Color color = result.Ok ? Color.LightGreen : Color.Orange;
        DebugConsole.NewMessage(result.Text, color);
        if (client != null) { GameMain.Server?.SendConsoleMessage(result.Text, client, color); }
    }
}
