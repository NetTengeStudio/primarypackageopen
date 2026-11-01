using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    internal void RevivePlayer(Peer player, bool campaignOverride, Vector2 point) {
        if (!Multiplayer || !Allows(InteractionAction.RevivePlayers)) { return; }
        Send(InteractionPacket.RevivePlayer, output => { output.WriteString(player.Account); output.WriteUInt16(player.Character);
            output.WriteBoolean(campaignOverride); output.WriteSingle(point.X); output.WriteSingle(point.Y); });
    }
}

internal sealed partial class InteractionViews {
    private void OpenPlayerRevival() {
        var window = OpenCentered("Возрождение игроков", 520, 420);
        var players = client.Peers.ToArray();
        var list = window.Add(new VirtualList(i => players[i].Name + (players[i].Character == 0 ? " · тело отсутствует" : " · тело #" + players[i].Character)) { Count = players.Length, FillHeight = true });
        var policy = window.Add(new Dropdown(new[] { "Соблюдать правила режима", "Административное исключение кампании / permadeath" }));
        window.Add(new Text("Существующее тело оживает. После удаления тела создаётся новое с сохранённой личностью, без восстановления утраченного инвентаря.") { Wrap = true, Height = 70 });
        window.Add(new Button("Выбрать точку в помещении и возродить", _ => {
            if ((uint)list.SelectedIndex >= (uint)players.Length) { return; }
            var player = players[list.SelectedIndex]; bool rules = policy.SelectedIndex == 1;
            window.Visible = false;
            client.PickWorld(point => { window.Visible = true; SceneTry(() => client.RevivePlayer(player, rules, point)); }, () => window.Visible = true);
        }));
    }
}
