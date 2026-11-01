namespace PrimaryPackage.Economy {
    public enum OrderState : byte {
        Accepted = 0,
        InProgress = 1,
        AwaitingShuttle = 2,
        Marked = 3,
        Collected = 4,
        Failed = 5,
    }
    public static class OrderStateExtensions {
        public static string DisplayName(this OrderState state) => state switch {
            OrderState.Accepted => OrderText.Get("pp.order.state.accepted", "Принят"),
            OrderState.InProgress => OrderText.Get("pp.order.state.inprogress", "В процессе"),
            OrderState.AwaitingShuttle => OrderText.Get("pp.order.state.awaiting", "Ожидание челнока"),
            OrderState.Marked => OrderText.Get("pp.order.state.arrived", "Челнок прибыл"),
            OrderState.Collected => OrderText.Get("pp.order.state.collected", "Забран"),
            OrderState.Failed => OrderText.Get("pp.order.state.failed", "Просрочен"),
            _ => OrderText.Get("pp.order.state.unknown", "неизвестно"),
        };
        public static string CheckerStatusKey(this OrderState state) => state switch {
            OrderState.Accepted => "pp.order.checker.status.idle",
            OrderState.InProgress => "pp.order.checker.status.inprogress",
            OrderState.AwaitingShuttle => "pp.order.checker.status.awaiting",
            OrderState.Marked => "pp.order.checker.status.arrived",
            OrderState.Collected => "pp.order.checker.status.idle",
            OrderState.Failed => "pp.order.checker.status.failed",
            _ => "pp.order.checker.status.idle",
        };
    }
}
