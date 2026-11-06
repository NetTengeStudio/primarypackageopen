using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

internal sealed partial class InteractionViews : IDisposable {
    private readonly InteractionClient client;
    private GUIWindow messageWindow, errorWindow;
    private Text errorText;
    private string conversation = "";
    private readonly List<PrivateMessage> history = new();
    private readonly List<(GUIWindow Window, double Until)> notifications = new();
    private static Vector2 Pointer => GUIRuntime.Instance.Input.Pointer;
    public InteractionViews(InteractionClient client) {
        this.client = client;
        client.MessagesChanged += MessagesChanged;
        client.AnnouncementsChanged += GMAnnouncementsChanged;
    }
    private static GUIWindow OpenCentered(string title, float width, float height) {
        var layout = GUIRuntime.Instance.Layout;
        return GUIRuntime.Instance.Windows.Open(title, new Vector2(Math.Max(0, (layout.Viewport.X - width * layout.Scale) / 2), Math.Max(0, (layout.Viewport.Y - height * layout.Scale) / 2)), width, height);
    }
    public void Notify(PrivateMessage message) {
        var window = GUIRuntime.Instance.Windows.Notify("Сообщение · " + message.SenderName, new Vector2(GameMain.GraphicsWidth - 380 * Barotrauma.GUI.Scale, 60 * Barotrauma.GUI.Scale));
        notifications.Add((window, GUIRuntime.Now + 8));
        string preview = message.Text.Length > 60 ? message.Text.Substring(0, 60) + "…" : message.Text;
        window.Add(new Text(preview));
        window.Add(new Button("Открыть переписку", _ => { window.Close(); OpenMessages(null, ""); SetConversation(message.Kind, message.Kind == MessageKind.Private ? message.From : message.Thread, message.SenderName); }));
    }
    public void Error(string message) {
        if (errorWindow is { Disposed: false, Closing: false }) { errorText.Text = message; return; }
        var window = GUIRuntime.Instance.Windows.OpenPopup("Не удалось выполнить действие", Pointer, 420);
        errorWindow = window;
        errorText = window.Add(new Text(message) { Height = 80, Wrap = true });
        window.Add(new Button("Закрыть", _ => window.Close()));
    }
    public void Update() {
        if (messagesDirty && messageWindow is { Closing: false, Disposed: false }) { RefreshMessages(); }
        double now = GUIRuntime.Now;
        for (int i = notifications.Count - 1; i >= 0; i--) {
            var notification = notifications[i];
            if (notification.Window.Disposed || now >= notification.Until) { notification.Window.Close(); notifications.RemoveAt(i); }
        }
    }
    private void MessageTry(Action action) {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException) { Error(error.Message); }
    }
    public void Reset() {
        SaveInteractionView();
        gmMessageWindow?.Dispose(); gmMessageWindow = null; gmMessageHistory = null; gmMessageStatus = null;
        messageWindow?.Dispose(); errorWindow?.Dispose();
        messageWindow = errorWindow = null;
        transcript = null; peersList = null; composer = null; errorText = null;
        headerCache.Clear(); history.Clear();
        for (int i = 0; i < notifications.Count; i++) { notifications[i].Window.Dispose(); }
        notifications.Clear();
    }
    public void Dispose() {
        client.MessagesChanged -= MessagesChanged;
        client.AnnouncementsChanged -= GMAnnouncementsChanged;
        Reset();
    }
}
