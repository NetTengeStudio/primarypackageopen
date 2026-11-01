using Barotrauma;
using Barotrauma.Items.Components;
namespace PrimaryPackage.Economy {
    public partial class StationIncomeSensorComponent : ItemComponent {
        [Serialize(1000, IsPropertySaveable.No, description: "Кредитов, начисляемых станции за каждый полный час активного сигнала на входе.")]
        public int IncomePerHour { get; set; }
        [Serialize(15.0f, IsPropertySaveable.No, description: "Период начисления дохода в минутах игрового времени.")]
        public float PayoutIntervalMinutes { get; set; }
        [Serialize("Автоматический доход станции", IsPropertySaveable.No, description: "Комментарий, записываемый в журнал при автоматическом начислении дохода.")]
        public string Comment { get; set; }
        public StationIncomeSensorComponent(Item item, ContentXElement element) : base(item, element) {
            IsActive = true;
        }
    }
}
