using Barotrauma;
using Barotrauma.Items.Components;
namespace PrimaryPackage.Economy {
    public partial class StationIncomeSensorComponent {
        private const string InputName = "signal_in";
        private const double SignalTimeoutSeconds = 1.5;
        private bool lastSignalOn;
        private double lastSignalTime = double.NegativeInfinity;
        private double accumulatedSeconds;
        private double moneyRemainder;
        public override void ReceiveSignal(Signal signal, Connection connection) {
            if (connection?.Name != InputName) { return; }
            lastSignalOn = !string.IsNullOrEmpty(signal.value) && signal.value != "0";
            lastSignalTime = Timing.TotalTime;
        }
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            if (item.Condition <= 0.0f) { accumulatedSeconds = 0.0; return; }
            bool on = lastSignalOn && (Timing.TotalTime - lastSignalTime) <= SignalTimeoutSeconds;
            if (!on || IncomePerHour == 0) { return; }
            double periodSeconds = System.Math.Max(1.0, PayoutIntervalMinutes * 60.0);
            accumulatedSeconds += deltaTime;
            bool credited = false;
            while (accumulatedSeconds >= periodSeconds) {
                accumulatedSeconds -= periodSeconds;
                moneyRemainder += IncomePerHour * (periodSeconds / 3600.0);
                int whole = (int)moneyRemainder;
                if (whole != 0) {
                    moneyRemainder -= whole;
                    StationEconomyState.ApplyTransaction(null, whole, Comment);
                    credited = true;
                }
            }
            if (credited) { EconomyNetwork.BroadcastAll(); }
        }
    }
}
