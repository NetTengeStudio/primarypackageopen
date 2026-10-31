using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using HarmonyLib;
namespace PrimaryPackage.Economy {
    public sealed class OrderBoardPlugin : PrimaryPackage.Core.IModule {
        private DebugConsole.Command listCmd, approveCmd, rejectCmd, lockCmd, unlockCmd, manualApprovalCmd;
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            Register(ref manualApprovalCmd, "economy_order_manual_approval", "economy_order_manual_approval <on|off>",
                args => ExecuteManualApproval(null, args), (c, _, args) => { if (HasPermission(c)) ExecuteManualApproval(c, args); });
            Register(ref listCmd, "economy_order_list", "economy_order_list",
                args => ExecuteList(null), (c, _, args) => { if (HasPermission(c)) ExecuteList(c); });
            Register(ref approveCmd, "economy_order_approve", "economy_order_approve <id>",
                args => ExecuteApprove(null, args), (c, _, args) => { if (HasPermission(c)) ExecuteApprove(c, args); });
            Register(ref rejectCmd, "economy_order_reject", "economy_order_reject <id>",
                args => ExecuteReject(null, args), (c, _, args) => { if (HasPermission(c)) ExecuteReject(c, args); });
            Register(ref lockCmd, "economy_dock_lock", "economy_dock_lock <zoneId>",
                args => ExecuteLock(null, args, true), (c, _, args) => { if (HasPermission(c)) ExecuteLock(c, args, true); });
            Register(ref unlockCmd, "economy_dock_unlock", "economy_dock_unlock <zoneId>",
                args => ExecuteLock(null, args, false), (c, _, args) => { if (HasPermission(c)) ExecuteLock(c, args, false); });
        }
        public void Release() {
            Drop(ref listCmd); Drop(ref approveCmd); Drop(ref rejectCmd); Drop(ref lockCmd); Drop(ref unlockCmd);
            Drop(ref manualApprovalCmd);
        }
        private static void Register(ref DebugConsole.Command field, string name, string help,
            Action<string[]> serverAction,
            Action<Client, Microsoft.Xna.Framework.Vector2, string[]> clientAction) {
            field = new DebugConsole.Command(name, help, serverAction);
            PrimaryPackage.Core.ModCommands.Adopt(field);
            DebugConsole.AssignOnClientRequestExecute(name, clientAction);
        }
        private static void Drop(ref DebugConsole.Command cmd) {
            if (cmd == null) { return; }
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
            if (c != null) { GameMain.Server?.SendConsoleMessage(text, c, color); }
            else { DebugConsole.NewMessage(text, color); }
        }
        internal static void ExecuteList(Client c) {
            var pending = OrderRegistry.Pending;
            var claims = OrderRegistry.Claims;
            if (pending.Count == 0 && claims.Count == 0) { Reply(c, "Нет заявок на закупку и нарядов на доставку.", Color.Yellow); return; }
            string mode = OrderRegistry.ManualApprovalRequired ? "ручное одобрение" : "автоодобрение";
            Reply(c, $"Режим закупок {mode}.", Color.Cyan);
            if (pending.Count > 0) {
                Reply(c, $"Заявки на закупку ({pending.Count})", Color.Cyan);
                foreach (OrderRegistry.PendingOrder o in pending) {
                    string name = OrderSets.TryGet(o.OrderSetId, out OrderSet set) ? set.DisplayName : o.OrderSetId.Value;
                    string status = o.Approved ? "ОДОБРЕНО" : "ожидает";
                    Reply(c, $"  №{o.Id} | {name} | заявитель {o.RequestedBy} | {status}",
                        o.Approved ? Color.LightGreen : Color.White);
                }
            }
            if (claims.Count > 0) {
                Reply(c, $"Наряды на доставку ({claims.Count}, не выполнены)", Color.Cyan);
                foreach (OrderRegistry.DeliveryClaim cl in claims) {
                    string name = OrderSets.TryGet(cl.OrderSetId, out OrderSet set) ? set.DisplayName : cl.OrderSetId.Value;
                    Reply(c, $"  №{cl.Id} | {name} | заявитель {cl.RequestedBy}", Color.White);
                }
            }
        }
        internal static void ExecuteManualApproval(Client c, string[] args) {
            if (args.Length < 1 || (args[0] != "on" && args[0] != "off")) {
                Reply(c, $"Usage economy_order_manual_approval <on|off>. Сейчас {(OrderRegistry.ManualApprovalRequired ? "on" : "off")}.", Color.Yellow);
                return;
            }
            OrderRegistry.ManualApprovalRequired = args[0] == "on";
            Reply(c, OrderRegistry.ManualApprovalRequired
                ? "Закупки теперь требуют одобрения ГМ (economy_order_approve)."
                : "Закупки одобряются автоматически при достаточном балансе.", Color.LightGreen);
        }
        internal static void ExecuteApprove(Client c, string[] args) {
            if (args.Length < 1 || !int.TryParse(args[0], out int id)) { Reply(c, "Usage: economy_order_approve <id>", Color.Yellow); return; }
            if (!OrderRegistry.TryGetPending(id, out OrderRegistry.PendingOrder order))
            { Reply(c, $"Заявка №{id} не найдена.", Color.Red); return; }
            if (order.Approved)
            { Reply(c, $"Заявка №{id} уже одобрена.", Color.Yellow); return; }
            if (!OrderSets.TryGet(order.OrderSetId, out OrderSet set))
            { Reply(c, $"Комплект заявки №{id} больше не существует.", Color.Red); return; }
            if (StationEconomyState.Balance < set.BuyPrice) {
                Reply(c, $"Недостаточно средств закупка №{id} стоит {set.BuyPrice:N0} кр., на балансе {StationEconomyState.Balance:N0} кр.", Color.Red);
                return;
            }
            OrderRegistry.TryApprove(id, set.BuyPrice);
            StationEconomyState.ApplyTransaction(null, -set.BuyPrice, $"Закупка №{id} «{set.DisplayName}» списание при одобрении ГМ");
            EconomyNetwork.BroadcastAll();
            Reply(c, $"Заявка №{id} одобрена, списано {set.BuyPrice:N0} кр. Будет исполнена на свободной стыковочной зоне.", Color.LightGreen);
        }
        internal static void ExecuteReject(Client c, string[] args) {
            if (args.Length < 1 || !int.TryParse(args[0], out int id)) { Reply(c, "Usage: economy_order_reject <id>", Color.Yellow); return; }
            if (!OrderRegistry.TryReject(id, out OrderRegistry.PendingOrder removed))
            { Reply(c, $"Заявка №{id} не найдена.", Color.Red); return; }
            if (removed.Approved && removed.ApprovedReward > 0) {
                string name = OrderSets.TryGet(removed.OrderSetId, out OrderSet set) ? set.DisplayName : removed.OrderSetId.Value;
                StationEconomyState.ApplyTransaction(null, removed.ApprovedReward, $"Возврат за отклонённую закупку №{id} «{name}»");
                EconomyNetwork.BroadcastAll();
                Reply(c, $"Заявка №{id} отклонена, возвращено {removed.ApprovedReward:N0} кр.", Color.LightGreen);
                return;
            }
            Reply(c, $"Заявка №{id} отклонена.", Color.LightGreen);
        }
        internal static void ExecuteLock(Client c, string[] args, bool locking) {
            if (args.Length < 1 || !int.TryParse(args[0], out int zoneId)) {
                Reply(c, $"Usage economy_dock_{(locking ? "lock" : "unlock")} <zoneId>", Color.Yellow);
                return;
            }
            if (Entity.FindEntityByID((ushort)zoneId) is not Item zoneItem || zoneItem.GetComponent<DockZoneComponent>() == null) {
                Reply(c, $"Стыковочная зона с ID {zoneId} не найдена.", Color.Red);
                return;
            }
            if (locking) { OrderRegistry.LockZone(zoneId); Reply(c, $"Стыковочная зона {zoneId} заблокирована.", Color.LightGreen); }
            else { OrderRegistry.UnlockZone(zoneId); Reply(c, $"Стыковочная зона {zoneId} разблокирована.", Color.LightGreen); }
        }
    }
}
