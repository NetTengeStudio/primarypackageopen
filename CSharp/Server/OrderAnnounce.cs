using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public static class OrderAnnounce {
        private static string Channel() => OrderText.Get("pp.order.announce.channel", "[ ГРОМКОГОВОРИТЕЛИ ]");
        private static void Broadcast(string body) {
            GameServer server = GameMain.Server;
            if (server == null || string.IsNullOrEmpty(body)) { return; }
            string channel = Channel();
            foreach (Client client in server.ConnectedClients) {
                server.SendDirectChatMessage(ChatMessage.Create(channel, body, ChatMessageType.Radio, sender: null), client);
            }
        }
        public static void ShuttleApproach(string callsign, int distanceMeters, int timeSeconds) {
            Broadcast(OrderText.Format("pp.order.announce.shuttle_approach",
                " - «Внимание всему персоналу станции! Автономный транспортный челнок «[callsign]» выходит на стыковочный курс. Дистанция [distance] м, время контакта [time] секунд.» ",
                ("[callsign]", callsign ?? "?"),
                ("[distance]", distanceMeters.ToString()),
                ("[time]", timeSeconds.ToString())));
        }
        public static void ShuttleDeparted(string callsign, int orderNumber) {
            Broadcast(OrderText.Format("pp.order.announce.shuttle_departed",
                " - «Автономный транспортный челнок «[callsign]» принял груз (наряд №[num]) и отстыковывается.» ",
                ("[callsign]", callsign ?? "?"),
                ("[num]", orderNumber.ToString())));
        }
        public static void ShuttleFailed(string callsign, int orderNumber) {
            Broadcast(OrderText.Format("pp.order.announce.shuttle_failed",
                " - «Автономный транспортный челнок «[callsign]» отстыковался без груза. Наряд №[num] провален.» ",
                ("[callsign]", callsign ?? "?"),
                ("[num]", orderNumber.ToString())));
        }
    }
}
