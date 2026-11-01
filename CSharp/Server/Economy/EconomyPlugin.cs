using System;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Economy {
    public sealed class EconomyPlugin : PrimaryPackage.Core.IModule {
                private DebugConsole.Command addCmd, setCmd, historyCmd, clearHistoryCmd, setLimitsCmd, statsCmd;
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            try {
                harmony.Patch(
                    AccessTools.Method(typeof(GameServer), nameof(GameServer.Update), new[] { typeof(float) }),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(EconomyPlugin), nameof(GameServerUpdatePostfix))));
            }
            catch (Exception e) {
                DebugConsole.ThrowError("economy Не удалось поставить патч GameServer.Update", e);
            }
            EconomyPersistence.Initialize();
            Register(ref addCmd, "economy_add",
                "economy_add <дата|-> <сумма> <комментарий...>",
                args => ExecuteAdd(null, args),
                (c, _, args) => { if (HasPermission(c)) ExecuteAdd(c, args); });
            Register(ref setCmd, "economy_set",
                "economy_set <сумма>",
                args => ExecuteSet(null, args),
                (c, _, args) => { if (HasPermission(c)) ExecuteSet(c, args); });
            Register(ref historyCmd, "economy_history",
                "economy_history [N]",
                args => ExecuteHistory(null, args),
                (c, _, args) => { if (HasPermission(c)) ExecuteHistory(c, args); });
            Register(ref clearHistoryCmd, "economy_clear_history",
                "economy_clear_history",
                args => ExecuteClearHistory(null),
                (c, _, args) => { if (HasPermission(c)) ExecuteClearHistory(c); });
            Register(ref setLimitsCmd, "economy_set_limits",
                "economy_set_limits <макс_за_операцию> <кулдаун_сек> [кулдаун_заказов_сек]",
                args => ExecuteSetLimits(null, args),
                (c, _, args) => { if (HasPermission(c)) ExecuteSetLimits(c, args); });
            Register(ref statsCmd, "economy_stats",
                "economy_stats",
                args => ExecuteStats(null),
                (c, _, args) => { if (HasPermission(c)) ExecuteStats(c); });
        }
        public void Release() {
            EconomyPersistence.Flush();
            Drop(ref addCmd); Drop(ref setCmd); Drop(ref historyCmd); Drop(ref clearHistoryCmd);
            Drop(ref setLimitsCmd); Drop(ref statsCmd);
        }
        private static void GameServerUpdatePostfix() {
            if (GameMain.Server == null) { return; }
            try {
                OrderRegistry.TickExpiryGlobal(Timing.TotalTime);
                EconomyPersistence.SaveIfDue(Timing.TotalTime);
            }
            catch (Exception e) {
                DebugConsole.ThrowError("economy ошибка в тике GameServer.Update", e);
            }
        }
        private static void Register(ref DebugConsole.Command field, string name, string help,
            Action<string[]> serverAction,
            Action<Client, Microsoft.Xna.Framework.Vector2, string[]> clientAction) {
            field = new DebugConsole.Command(name, help, serverAction);
            PrimaryPackage.Core.ModCommands.Adopt(field);
            DebugConsole.AssignOnClientRequestExecute(name, clientAction);
        }
        private static void Drop(ref DebugConsole.Command cmd) {
            if (cmd == null) return;
            PrimaryPackage.Core.ModCommands.Drop(cmd);
            cmd = null;
        }
        private static bool HasPermission(Client c) {
            if (c != null && !c.HasPermission(ClientPermissions.ConsoleCommands)) {
                GameMain.Server?.SendConsoleMessage("Недостаточно прав. Требуется ConsoleCommands.", c, Color.Red);
                return false;
            }
            return true;
        }
        private static void Reply(Client c, string text, Color color) {
            if (c != null) GameMain.Server?.SendConsoleMessage(text, c, color);
            else DebugConsole.NewMessage(text, color);
        }
        internal static void ExecuteAdd(Client c, string[] args) {
            if (args.Length < 2) { Reply(c, "Usage: economy_add <дата|-> <сумма> <комментарий...>", Color.Yellow); return; }
            string date = args[0] == "-" ? null : args[0];
            if (!int.TryParse(args[1], out int amount)) { Reply(c, $"Некорректная сумма '{args[1]}'", Color.Red); return; }
            string comment = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "";
            StationEconomyState.ApplyTransaction(date, amount, comment);
            EconomyNetwork.BroadcastAll();
            string kind = amount >= 0 ? "ПРИХОД" : "РАСХОД";
            Reply(c, $"Записано {kind} {amount:+#,##0;-#,##0;0} кр. Новый баланс {StationEconomyState.Balance:N0} кр.", Color.LightGreen);
        }
        internal static void ExecuteSet(Client c, string[] args) {
            if (args.Length < 1 || !int.TryParse(args[0], out int v)) { Reply(c, "Usage: economy_set <сумма>", Color.Yellow); return; }
            StationEconomyState.SetBalance(v);
            EconomyNetwork.BroadcastAll();
            Reply(c, $"Баланс станции выставлен {v:N0} кр.", Color.LightGreen);
        }
        internal static void ExecuteHistory(Client c, string[] args) {
            int count = 10;
            if (args.Length > 0) int.TryParse(args[0], out count);
            count = Math.Max(1, count);
            var entries = StationEconomyState.History.AsEnumerable().Reverse().Take(count).Reverse().ToList();
            if (entries.Count == 0) { Reply(c, "Журнал пуст.", Color.Yellow); return; }
            foreach (LedgerEntry e in entries) {
                string kind = e.Amount >= 0 ? "ПРИХОД" : "РАСХОД";
                Reply(c, $"[{e.Date}] | {kind} | {e.Amount:+#,##0;-#,##0;0} кр. | {e.Comment}", e.Amount >= 0 ? Color.LightGreen : Color.OrangeRed);
            }
            Reply(c, $"Текущий баланс {StationEconomyState.Balance:N0} кр.", Color.Cyan);
        }
        internal static void ExecuteClearHistory(Client c) {
            StationEconomyState.ClearHistory();
            EconomyNetwork.BroadcastAll();
            Reply(c, "Журнал операций очищен.", Color.LightGreen);
        }
        internal static void ExecuteSetLimits(Client c, string[] args) {
            if (args.Length < 2 || !int.TryParse(args[0], out int maxPerTx)
                || !double.TryParse(args[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double cooldown)) {
                Reply(c, "Usage: economy_set_limits <макс_за_операцию> <кулдаун_сек> [кулдаун_заказов_сек] (0 = без лимита)", Color.Yellow);
                return;
            }
            double? orderCooldown = null;
            if (args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double oc)) {
                orderCooldown = oc;
            }
            EconomyLimits.SetLimits(maxPerTx, cooldown, orderCooldown);
            string maxTxt = EconomyLimits.MaxWithdrawPerTransaction > 0 ? $"{EconomyLimits.MaxWithdrawPerTransaction:N0} кр." : "без лимита";
            string cdTxt = EconomyLimits.WithdrawCooldownSeconds > 0 ? $"{EconomyLimits.WithdrawCooldownSeconds:0.#} с" : "без кулдауна";
            string ordTxt = EconomyLimits.OrderCooldownSeconds > 0 ? $"{EconomyLimits.OrderCooldownSeconds:0.#} с" : "без кулдауна";
            Reply(c, $"Лимиты макс. снятие за операцию {maxTxt}, кулдаун снятия {cdTxt}, кулдаун заказов {ordTxt}.", Color.LightGreen);
        }
        internal static void ExecuteStats(Client c) {
            long income = 0, expense = 0;
            int count = StationEconomyState.History.Count;
            foreach (LedgerEntry e in StationEconomyState.History) {
                if (e.Amount >= 0) income += e.Amount;
                else expense += e.Amount;
            }
            Reply(c, $"Статистика экономики ({count} записей)", Color.Cyan);
            Reply(c, $"  Приход (всего) {income:+#,##0;-#,##0;0} кр.", Color.LightGreen);
            Reply(c, $"  Расход (всего) {expense:+#,##0;-#,##0;0} кр.", Color.OrangeRed);
            Reply(c, $"  Нетто по журналу {income + expense:+#,##0;-#,##0;0} кр.", Color.Cyan);
            Reply(c, $"  Текущий баланс {StationEconomyState.Balance:N0} кр.", Color.Cyan);
        }
    }
}
