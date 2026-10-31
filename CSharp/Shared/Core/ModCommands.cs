using System;
using System.Collections.Generic;

namespace PrimaryPackage.Core;

public static class ModCommands {
    private static readonly List<DebugConsole.Command> registered = new List<DebugConsole.Command>();

    public static DebugConsole.Command Register(string name, string help, Action<string[]> onExecute) =>
        Register(name, help, onExecute, localOnClient: false);

    public static DebugConsole.Command Register(string name, string help, Action<string[]> onExecute,
        bool localOnClient, bool relayToServer = true, Func<string[][]> validArgs = null, bool isCheat = false) {
        if (string.IsNullOrEmpty(name) || onExecute == null) { return null; }
        DebugConsole.Command existing = Find(name);
        if (existing != null) { return existing; }
        DebugConsole.Command command = new DebugConsole.Command(name, help, onExecute, validArgs, isCheat);
#if CLIENT
        if (localOnClient && relayToServer)
        {
            relayToServer = false;
            ModLog.Once($"{name} asked to both run locally and relay, kept local");
        }
        command.RelayToServer = relayToServer;
        if (localOnClient) { command.OnClientExecute = onExecute; }
#endif
        DebugConsole.Commands.Add(command);
        registered.Add(command);
        return command;
    }

    public static void Adopt(DebugConsole.Command command) {
        if (command == null || registered.Contains(command)) { return; }
        if (!DebugConsole.Commands.Contains(command)) { DebugConsole.Commands.Add(command); }
        registered.Add(command);
    }

    public static void Drop(DebugConsole.Command command) {
        if (command == null) { return; }
        DebugConsole.Commands.Remove(command);
        registered.Remove(command);
    }

    public static void Release() {
        for (int i = registered.Count - 1; i >= 0; i--) { DebugConsole.Commands.Remove(registered[i]); }
        registered.Clear();
    }

    private static DebugConsole.Command Find(string name) {
        Identifier first = name.Split('|')[0].ToIdentifier();
        for (int i = 0; i < registered.Count; i++) {
            if (registered[i].Names.Contains(first)) { return registered[i]; }
        }
        return null;
    }
}
