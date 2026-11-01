using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;

namespace PrimaryPackage.VideoScreens {
    public partial class VideoScreen : Powered, IClientSerializable, IServerSerializable {
        [Serialize("", IsPropertySaveable.No, description: "Путь к видеофайлу (.webm) относительно папки мода.")]
        public string File { get; set; }
        [Serialize(60f, IsPropertySaveable.No, description: "Длительность ролика в секундах.")]
        public float Duration { get; set; }
        [Serialize(3f, IsPropertySaveable.No, description: "Пауза между повторами ролика, сек.")]
        public float LoopDelay { get; set; }
        [Serialize(600f, IsPropertySaveable.No, description: "Радиус воспроизведения/слышимости, мировых пикселей.")]
        public float SoundRange { get; set; }
        [Editable(MinValueFloat = 0f, MaxValueFloat = 1f, DecimalCount = 2)]
        [Serialize(1f, IsPropertySaveable.Yes, description: "Громкость звука ролика (0-1).")]
        public float Volume { get; set; }
        [Serialize(128f, IsPropertySaveable.No, description: "Ширина области видео в пикселях текстуры рамки.")]
        public float ScreenWidth { get; set; }
        [Serialize(72f, IsPropertySaveable.No, description: "Высота области видео в пикселях текстуры рамки.")]
        public float ScreenHeight { get; set; }

        public readonly struct SyncEventData : ItemComponent.IEventData {}
        public readonly struct FullSyncRequestEventData : ItemComponent.IEventData {}

        private const double TogglePressGap = 0.25;

        private double lastToggleSignalTime = double.NegativeInfinity;
        private bool lastEffectiveOn;

        private bool IsAuthority => GameMain.NetworkMember is not { IsClient: true };
        private bool EffectiveOn => IsActive && HasPower;
        public float CycleLength => System.Math.Max(0.1f, Duration) + System.Math.Max(0f, LoopDelay);
        public override bool UpdateWhenInactive => true;

        public VideoScreen(Item item, ContentXElement element) : base(item, element) {
            IsActive = false;
        }

        protected static float Wrap(double value, double len) {
            if (len <= 0.0) { return 0f; }
            double e = value % len;
            return (float)(e < 0 ? e + len : e);
        }

        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<SyncEventData>(data, out _)
            || TryExtractEventData<FullSyncRequestEventData>(data, out _);

        public override void OnItemLoaded() {
            base.OnItemLoaded();
            lastEffectiveOn = EffectiveOn;
            OnItemLoadedProjSpecific();
        }

        public override void ReceiveSignal(Signal signal, Connection connection) {
            if (!IsAuthority || connection == null) { return; }
            bool signalOn = !string.IsNullOrEmpty(signal.value) && signal.value != "0";
            if (connection.Name == "toggle") {
                if (!signalOn) { return; }
                double now = Timing.TotalTime;
                if (now - lastToggleSignalTime > TogglePressGap) {
                    SetActive(!IsActive);
                }
                lastToggleSignalTime = now;
            } else if (connection.Name == "set_state") {
                SetActive(signalOn);
            }
        }

        private void SetActive(bool active) {
            if (IsActive == active) { return; }
            IsActive = active;
            PushIfChanged();
        }

        private void PushIfChanged() {
            bool effective = EffectiveOn;
            if (effective != lastEffectiveOn) {
                lastEffectiveOn = effective;
                PushSync();
            }
        }

        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            if (IsAuthority) {
                PushIfChanged();
                PeriodicSync();
            }
            UpdateProjSpecific(deltaTime);
        }

        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) {}

        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void UpdateProjSpecific(float deltaTime);
        partial void OnItemLoadedProjSpecific();
        partial void PushSync();
        partial void PeriodicSync();
    }
}
