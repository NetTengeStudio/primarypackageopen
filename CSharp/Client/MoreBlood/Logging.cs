using System.IO;
using System.Runtime.CompilerServices;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace MoreBlood
{
  public partial class Mod
  {
    public static string WrapInColor(object msg, string color)
    {
      return $"‖color:{color}‖{msg}‖end‖";
    }

    public static void Log(object msg, Color? color = null)
    {
      color ??= Color.Cyan;
      LuaCsLogger.LogMessage($"{msg ?? "null"}", color * 0.8f, color);
    }

    public static void Warning(object msg, Color? color = null)
    {
      color ??= Color.Yellow;
      LuaCsLogger.LogMessage($"{msg ?? "null"}", color * 0.8f, color);
    }

    public static string GetCallerFolderPath([CallerFilePath] string source = "") => Path.GetDirectoryName(source);
  }
}
