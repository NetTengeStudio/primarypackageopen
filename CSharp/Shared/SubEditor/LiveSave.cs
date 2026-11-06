using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Barotrauma;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

public static class LiveSave
{
    public const string ServerFolder = "Submarines/LiveEditor";

    public static SubmarineInfo Capture(Submarine sub)
    {
        SubmarineInfo info = null;
        WithoutEditorScreen(() => info = new SubmarineInfo(sub));
        var linked = new List<MapEntity>();
        for (int i = 0; i < MapEntity.MapEntityList.Count; i++)
        {
            MapEntity e = MapEntity.MapEntityList[i];
            if (e != null && !e.Removed && e.Submarine == sub && e is LinkedSubmarine && e.ShouldBeSaved) { linked.Add(e); }
        }
        linked.Sort((a, b) => a.ID.CompareTo(b.ID));
        int linkIdx = 0;
        foreach (var element in info.SubmarineElement.Elements())
        {
            if (element.Name.LocalName.Equals(nameof(LinkedSubmarine), StringComparison.OrdinalIgnoreCase) && linkIdx < linked.Count)
            {
                element.SetAttributeValue("ID", linked[linkIdx++].ID);
            }
        }
        return info;
    }

    private static void WithoutEditorScreen(Action action)
    {
#if CLIENT
        Screen previous = Screen.Selected;
        bool swap = previous != null && previous == GameMain.SubEditorScreen;
        if (swap) { Screen.Selected = GameMain.GameScreen; }
        try { action(); }
        finally { if (swap) { Screen.Selected = previous; } }
#else
        action();
#endif
    }

    public static bool Write(Submarine sub, string filePath, MemoryStream preview = null)
    {
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (folder != null) { Directory.CreateDirectory(folder); }
            using SubmarineInfo info = Capture(sub);
            info.FilePath = filePath; info.Name = Path.GetFileNameWithoutExtension(filePath);
            info.SaveAs(filePath, preview);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException)
        {
            ModLog.Once($"subeditor: cannot save \"{filePath}\": {e.Message}");
            return false;
        }
    }
}
