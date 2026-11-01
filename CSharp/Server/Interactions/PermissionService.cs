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
    private readonly AtomicStore<State> store;
    private readonly Dictionary<(string, InteractionAction), bool> assignments = new();
    private State state;
    public PermissionService(string directory) {
        store = new AtomicStore<State>(Path.Combine(directory, "permissions.json"), Validate);
        state = store.Load(() => new State(), error => ModLog.Error("Permissions recovered from backup: " + error.Message));
        foreach (Assignment grant in state.Grants) {
            if (!ValidAccount(grant.Account) || !Enum.IsDefined(grant.Action) || !assignments.TryAdd((grant.Account, grant.Action), grant.Allowed)) { throw new InvalidDataException("Invalid permission assignment"); }
        }
    }
    private static void Validate(State value) {
        if (value == null || value.Version != 1 || value.Grants == null || value.Audit == null || value.Grants.Count > 8192 || value.Audit.Count > 4096) { throw new InvalidDataException("Invalid permission state"); }
        var unique = new HashSet<(string, InteractionAction)>();
        foreach (Assignment grant in value.Grants) { if (!Valid(grant) || grant.Inherited || !unique.Add((grant.Account, grant.Action))) { throw new InvalidDataException("Invalid permission assignment"); } }
        foreach (Assignment entry in value.Audit) { if (!Valid(entry)) { throw new InvalidDataException("Invalid permission audit"); } }
    }
    private static bool Valid(Assignment value) => value != null && ValidAccount(value.Account) && ValidAccount(value.By) && Enum.IsDefined(value.Action) && value.ChangedAt > 0 && value.ChangedAt <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
    public IReadOnlyList<Assignment> Grants => state.Grants;
    public IReadOnlyList<Assignment> Audit => state.Audit;
    public static string Account(Client client) => client != null && client.AccountId.TryUnwrap(out var account) ? account.ToString() : "";
    public static bool ValidAccount(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim()) { return false; }
        for (int i = 0; i < value.Length; i++) { if (char.IsControl(value[i])) { return false; } }
        return true;
    }
    public static bool Owner(Client client) => client?.Connection != null && client.Connection == GameMain.Server?.OwnerConnection;
    public bool Allows(Client client, InteractionAction action) {
        if (client?.Connection == null || !Enum.IsDefined(action)) { return false; }
        if (Owner(client)) { return true; }
        string account = Account(client);
        if (account.Length == 0) { return false; }
        if (assignments.TryGetValue((account, action), out bool allowed)) { return allowed; }
        if (action is InteractionAction.ViewCharacter or InteractionAction.SendPrivateMessage) { return true; }
        if (action == InteractionAction.ManagePermissions) { return client.HasPermission(ClientPermissions.ManagePermissions); }
        string command = action switch {
            InteractionAction.QueryCharacters => "nearchars",
            InteractionAction.QueryItems => "nearitems",
            InteractionAction.SpawnCharacter or InteractionAction.ControlCharacter => "spawnchar",
            InteractionAction.FlickerLight => "light",
            InteractionAction.ViewGMFeed => "ahelp",
            InteractionAction.ManageEntities => "edititem",
            InteractionAction.DirectEvents => "spawnchar",
            InteractionAction.SendGMMessage => "message",
            InteractionAction.RevivePlayers => "revive",
            InteractionAction.ManageKits => "createkit",
            _ => ""
        };
        return CommandAllowed(client, command);
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
    public void Set(Client actor, string account, InteractionAction action, bool? allowed) {
        if (!Allows(actor, InteractionAction.ManagePermissions) || !ValidAccount(account) || !Enum.IsDefined(action)) { throw new UnauthorizedAccessException("Permission change denied"); }
        if (action == InteractionAction.ManagePermissions && !Owner(actor)) { throw new UnauthorizedAccessException("Only the owner can delegate permission management"); }
        if (account == Account(actor)) { throw new UnauthorizedAccessException("Self permission changes are forbidden"); }
        if (allowed == true && !Owner(actor) && !Allows(actor, action)) { throw new UnauthorizedAccessException("Cannot delegate a capability the actor does not hold"); }
        var grants = new List<Assignment>(state.Grants);
        grants.RemoveAll(value => value.Account == account && value.Action == action);
        var change = new Assignment { Account = account, Action = action, Allowed = allowed ?? false, Inherited = !allowed.HasValue, By = Account(actor), ChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        if (allowed.HasValue) { grants.Add(change); }
        if (grants.Count > 8192) { throw new InvalidOperationException("Permission capacity reached"); }
        var audit = new List<Assignment>(state.Audit);
        if (audit.Count == 4096) { audit.RemoveAt(0); }
        audit.Add(change);
        var next = new State { Grants = grants, Audit = audit };
        store.Save(next);
        state = next;
        if (allowed.HasValue) { assignments[(account, action)] = allowed.Value; } else { assignments.Remove((account, action)); }
    }
}
