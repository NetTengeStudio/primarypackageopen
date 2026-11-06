using INet;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.Economy {
    internal static class EconomyLimits {
        private static readonly RateLimit withdraw = new RateLimit(10.0);
        private static readonly RateLimit order = new RateLimit(30.0);

        public static int MaxWithdrawPerTransaction = 5000;

        public static double WithdrawCooldownSeconds => withdraw.Interval;

        public static double OrderCooldownSeconds => order.Interval;

        public static bool CheckAmountLimit(int amount, out string error) {
            error = null;
            if (MaxWithdrawPerTransaction > 0 && amount > MaxWithdrawPerTransaction) {
                error = OrderText.Format("pp.order.limit.exceeded",
                    $"Превышен лимит на операцию максимум {MaxWithdrawPerTransaction:N0} кр.",
                    ("[max]", MaxWithdrawPerTransaction.ToString("N0")));
                return false;
            }
            return true;
        }

        public static bool CheckCooldown(Client c, out string error) =>
            Check(withdraw, c, "pp.order.limit.cooldown_withdraw",
                "Слишком часто. Подождите {0} с перед следующим снятием.", out error);

        public static void RecordWithdraw(Client c) => withdraw.Record(c);

        public static bool CheckOrderCooldown(Client c, out string error) =>
            Check(order, c, "pp.order.limit.cooldown_order",
                "Слишком часто. Подождите {0} с перед следующим заказом.", out error);

        public static void RecordOrder(Client c) => order.Record(c);

        public static void SetLimits(int maxPerTx, double cooldownSec, double? orderCooldownSec = null) {
            MaxWithdrawPerTransaction = maxPerTx < 0 ? 0 : maxPerTx;
            withdraw.Interval = cooldownSec < 0 ? 0 : cooldownSec;
            if (orderCooldownSec.HasValue) { order.Interval = orderCooldownSec.Value < 0 ? 0 : orderCooldownSec.Value; }
        }

        private static bool Check(RateLimit limit, Client c, string key, string fallback, out string error) {
            error = null;
            double remaining = limit.Remaining(c);
            if (remaining <= 0.0) { return true; }
            string seconds = remaining.ToString("0.#");
            error = OrderText.Format(key, string.Format(fallback, seconds), ("[time]", seconds));
            return false;
        }
    }
}
