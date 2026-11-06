using System;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// What the original editor has no place for because it never edits a shared world: the link state, and adding or removing
/// whole submarines in the running round. Nothing needs saving: every change is in the round the moment the server takes it
/// (the server also keeps an autosave on disk), and the original editor's own Save (Ctrl+S) still writes a file on this machine.
/// </summary>
internal sealed class LivePanel
{
    private readonly LiveEditorScreen screen;
    private readonly GUIFrame frame;
    private readonly GUITextBlock status;

    public LivePanel(LiveEditorScreen screen)
    {
        this.screen = screen;
        var canvas = global::Barotrauma.GUI.Canvas;
        frame = new GUIFrame(new RectTransform(new Vector2(0.14f, 0.13f), canvas, Anchor.TopRight)
        {
            MinSize = new Point((int)(260 * global::Barotrauma.GUI.Scale), (int)(112 * global::Barotrauma.GUI.Scale)),
            AbsoluteOffset = new Point((int)(10 * global::Barotrauma.GUI.Scale), (int)(50 * global::Barotrauma.GUI.Scale)),
        }, "InnerFrame");
        var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.92f, 0.9f), frame.RectTransform, Anchor.Center)) { RelativeSpacing = 0.04f, Stretch = true };
        status = new GUITextBlock(new RectTransform(new Vector2(1f, 0.4f), layout.RectTransform), "", font: GUIStyle.SmallFont, wrap: true);
        Button(layout, "Добавить подлодку", OpenAdd);
        Button(layout, "Убрать подлодку", OpenRemove);
    }

    private static void Button(GUIComponent parent, string text, Action click) =>
        new GUIButton(new RectTransform(new Vector2(1f, 0.28f), parent.RectTransform), text) { OnClicked = (_, _) => { click(); return true; } };

    public void AddToGUIUpdateList()
    {
        int others = LiveView.ActivePeers;
        status.Text = SubEditorClient.State == LinkState.Live
            ? "Правки видны всем сразу, сервер сохраняет сам" + (others > 0 ? $"\nРедакторов рядом: {others}" : "")
            : "Подключение к серверу…";
        frame.AddToGUIUpdateList();
    }

    private static GUIMessageBox Dialog(string header, string[] buttons, Vector2 size) =>
        new GUIMessageBox(header, "", buttons.Select(b => (LocalizedString)b).ToArray(), size);

    private void OpenAdd()
    {
        var box = Dialog("Добавить подлодку", new[] { "Добавить", "Отмена" }, new Vector2(0.3f, 0.5f));
        var list = new GUIListBox(new RectTransform(new Vector2(1f, 0.75f), box.Content.RectTransform));
        foreach (SubmarineInfo info in SubmarineInfo.SavedSubmarines.Where(i => !i.IsFileCorrupted).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), list.Content.RectTransform) { MinSize = new Point(0, 22) }, info.Name) { UserData = info };
        }
        box.Buttons[0].OnClicked = (_, _) =>
        {
            if (list.SelectedData is not SubmarineInfo chosen) { return false; }
            if (!screen.AddSubmarine(chosen)) { return false; }
            box.Close();
            return true;
        };
        box.Buttons[1].OnClicked = box.Close;
    }

    private void OpenRemove()
    {
        var box = Dialog("Убрать подлодку", new[] { "Убрать", "Отмена" }, new Vector2(0.3f, 0.4f));
        var list = new GUIListBox(new RectTransform(new Vector2(1f, 0.75f), box.Content.RectTransform));
        foreach (SubSpawn spawned in SubRegistry.Spawned)
        {
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), list.Content.RectTransform) { MinSize = new Point(0, 22) }, spawned.Name) { UserData = spawned };
        }
        box.Buttons[0].OnClicked = (_, _) =>
        {
            if (list.SelectedData is not SubSpawn chosen) { return false; }
            SubEditorClient.SendRequest(OpKind.DeleteSub, new[] { chosen.SubmarineId }, ReadOnlySpan<byte>.Empty, null);
            box.Close();
            return true;
        };
        box.Buttons[1].OnClicked = box.Close;
    }
}
