using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using Barotrauma.Networking;
using MoonSharp.Interpreter;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed class PermissionService {
    public sealed class Assignment {
        public string Account { get; set; } = "";
        public InteractionAction Action { get; set; }
        public bool Allowed { get; set; }
        public bool Inherited { get; set; }
        public string By { get; set; } = "";
        public long ChangedAt { get; set; }
    }
    public sealed class State {
        public int Version { get; set; } = 1;
        public List<Assignment> Grants { get; set; } = new();
        public List<Assignment> Audit { get; set; } = new();
    }
    private readonly Dictionary<(string, InteractionAction), bool> assignments = new();
    public PermissionService(string directory) {
        var store = new AtomicStore<State>(Path.Combine(directory, "permissions.json"), Validate);
        State state = store.Load(() => new State(), error => ModLog.Error("Permissions recovered from backup: " + error.Message));
        foreach (Assignment grant in state.Grants) {
            if (!ValidAccount(grant.Account) || (byte)grant.Action > 14 || !assignments.TryAdd((grant.Account, grant.Action), grant.Allowed)) { throw new InvalidDataException("Invalid permission assignment"); }
        }
    }
    private static void Validate(State value) {
        if (value == null || value.Version != 1 || value.Grants == null || value.Audit == null || value.Grants.Count > 8192 || value.Audit.Count > 4096) { throw new InvalidDataException("Invalid permission state"); }
        var unique = new HashSet<(string, InteractionAction)>();
        foreach (Assignment grant in value.Grants) { if (!Valid(grant) || grant.Inherited || !unique.Add((grant.Account, grant.Action))) { throw new InvalidDataException("Invalid permission assignment"); } }
        foreach (Assignment entry in value.Audit) { if (!Valid(entry)) { throw new InvalidDataException("Invalid permission audit"); } }
    }
    // v1 stores numeric action IDs 0..14, including grants for tools that have been removed.
    private static bool Valid(Assignment value) => value != null && ValidAccount(value.Account) && ValidAccount(value.By) && (byte)value.Action <= 14 && value.ChangedAt > 0 && value.ChangedAt <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
    public static string Account(Client client) => client != null && client.AccountId.TryUnwrap(out var account) ? account.ToString() : "";
    private static bool ValidAccount(string value) => InteractionText.ValidAccount(value);
    public static bool Owner(Client client) => client?.Connection != null && client.Connection == GameMain.Server?.OwnerConnection;
    public bool Allows(Client client, InteractionAction action) {
        if (client?.Connection == null || !Enum.IsDefined(action)) { return false; }
        if (Owner(client)) { return true; }
        string account = Account(client);
        if (account.Length == 0) { return false; }
        if (assignments.TryGetValue((account, action), out bool allowed)) { return allowed; }
        return action switch {
            InteractionAction.SendPrivateMessage => true,
            InteractionAction.FlickerLight => CommandAllowed(client, "light"),
            InteractionAction.ViewGMFeed => CommandAllowed(client, "ahelp"),
            InteractionAction.SendGMMessage => CommandAllowed(client, "message") || CommandAllowed(client, "msg"),
            _ => false
        };
    }
    internal static bool CommandAllowed(Client client, string command) {
        if (command.Length == 0) { return false; }
        if (client.HasPermission(ClientPermissions.ConsoleCommands)) {
            foreach (var permitted in client.PermittedConsoleCommands) { if (permitted.Names.Contains(command.ToIdentifier())) { return true; } }
        }
        Script lua = LuaCsSetup.Instance?.Lua;
        DynValue package = lua?.Globals.Get("package");
        DynValue loaded = package?.Type == DataType.Table ? package.Table.Get("loaded") : null;
        DynValue module = loaded?.Type == DataType.Table ? loaded.Table.Get("GMT_Scripts._UTILS.permissions") : null;
        DynValue function = module?.Type == DataType.Table ? module.Table.Get("HasCommandPermission") : null;
        if (function?.Type != DataType.Function && function?.Type != DataType.ClrFunction) { return false; }
        DynValue result = LuaCsSetup.Instance.CallLuaFunction(function, client, command);
        return result?.Type == DataType.Boolean && result.Boolean;
    }
    internal byte AnnouncementFormats(Client client) {
        if (!Allows(client, InteractionAction.SendGMMessage)) { return 0; }
        if (Owner(client) || assignments.TryGetValue((Account(client), InteractionAction.SendGMMessage), out bool allowed) && allowed || CommandAllowed(client, "message")) { return GMAnnouncement.AllFormats; }
        // Inheriting native msg alone must not grant intrusive popups or ScriptToast.
        return CommandAllowed(client, "msg") ? GMAnnouncement.ChatFormats : (byte)0;
    }
}
