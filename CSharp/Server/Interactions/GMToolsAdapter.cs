using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Interactions;

internal sealed class GMToolsAdapter : IDisposable {
    private sealed class Binding {
        public DebugConsole.Command Command;
        public Action<Client, Vector2, string[]> Previous, Installed;
    }
    private readonly PermissionService permissions;
    private readonly List<Binding> bindings = new();
    private readonly List<WorldQueryResult> results = new();
    public GMToolsAdapter(PermissionService permissions) { this.permissions = permissions; }
    public void TryAttach() {
        if (bindings.Count == 2) { return; }
        Bind(".nearchars", true);
        Bind(".nearitems", false);
    }
    private void Bind(string name, bool characters) {
        foreach (var binding in bindings) { if (binding.Command.Names.Contains(name.ToIdentifier())) { return; } }
        DebugConsole.Command command = null;
        foreach (var candidate in DebugConsole.Commands) { if (candidate.Names.Contains(name.ToIdentifier())) { command = candidate; break; } }
        if (command?.OnClientRequestExecute == null) { return; }
        var added = new Binding { Command = command, Previous = command.OnClientRequestExecute };
        added.Installed = (sender, cursor, args) => Query(sender, cursor, args, characters);
        command.OnClientRequestExecute = added.Installed;
        bindings.Add(added);
    }
    private void Query(Client sender, Vector2 cursor, string[] args, bool characters) {
        if (!permissions.Allows(sender, characters ? InteractionAction.QueryCharacters : InteractionAction.QueryItems)) {
            GameMain.Server.SendConsoleMessage("Primary Package: недостаточно разрешений", sender, Color.Red);
            return;
        }
        float radius = 100;
        if (args.Length > 0 && (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out radius) || !float.IsFinite(radius) || radius <= 0 || radius > 5000)) {
            GameMain.Server.SendConsoleMessage("Primary Package: радиус должен быть от 1 до 5000", sender, Color.Red);
            return;
        }
        WorldQueryService.Instance.Query(cursor, radius, characters, "", results, 10000);
        GameMain.Server.SendConsoleMessage("Объекты в радиусе " + radius.ToString(CultureInfo.InvariantCulture) + ": " + results.Count, sender);
        foreach (var result in results) {
            string label = result.Entity is Character character ? character.Name + " · " + character.TeamID : ((Item)result.Entity).Name + " · " + (((Item)result.Entity).Container?.Name ?? "Мир");
            GameMain.Server.SendConsoleMessage(label + " · ID " + result.Entity.ID, sender);
        }
    }
    public void Dispose() {
        foreach (var binding in bindings) { if (binding.Command.OnClientRequestExecute == binding.Installed) { binding.Command.OnClientRequestExecute = binding.Previous; } }
        bindings.Clear(); results.Clear();
    }
}
