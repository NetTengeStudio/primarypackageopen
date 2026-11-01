using System;
using System.Globalization;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Interactions;

// Translation is an explicit physical operation, not a serializable-property setter.
internal static class LiveItemTransform {
    internal const string PositionKey = "$position";
    internal readonly record struct PositionState(Guid Frame, Point Position);
    internal static string Encode(Guid frame, Point position) => frame.ToString("N") + "|" + position.X.ToString(CultureInfo.InvariantCulture) + "," + position.Y.ToString(CultureInfo.InvariantCulture);
    internal static PositionState Decode(string value) {
        if (value == null || value.Length > 80 || value.Length < 36 || value[32] != '|' || !Guid.TryParseExact(value.AsSpan(0, 32), "N", out Guid frame) || frame == Guid.Empty) { throw new ArgumentException("Недопустимая система координат операции"); }
        return new(frame, Parse(value[33..]));
    }
    internal static string Read(Item item) => item.Rect.X.ToString(CultureInfo.InvariantCulture) + "," + item.Rect.Y.ToString(CultureInfo.InvariantCulture);
    internal static Point Parse(string value) {
        int comma = value?.IndexOf(',') ?? -1;
        if (comma <= 0 || !int.TryParse(value.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(value.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            Math.Abs((long)x) > 10000000 || Math.Abs((long)y) > 10000000) { throw new ArgumentException("Недопустимая локальная позиция оборудования"); }
        return new Point(x, y);
    }
    internal static string Unavailable(Item item) {
        if (item is not { Removed: false, ParentInventory: null, Submarine: { Removed: false } }) { return "Требуется предмет в мире на действующей подлодке"; }
        if (item.StaticBodyConfig != null || item.StaticFixtures.Count > 0) { return "Объект имеет коллизии в физическом теле подлодки; требуется перестройка его fixtures"; }
        if (item.body is { Enabled: true }) { return "Сначала закрепите физически активный предмет"; }
        foreach (var component in item.Components) {
            if (component.GetType().Assembly != typeof(ItemComponent).Assembly) { return "Перемещение модового компонента ещё не зарегистрировано"; }
            if (component is Door or DockingPort or Wire or ItemContainer) { return "У объекта есть топология или вложенное содержимое; требуется специальная операция"; }
            if (component is ConnectionPanel panel) {
                if (panel.DisconnectedWires.Count > 0) { return "Сначала уберите незакреплённые провода"; }
                foreach (var connection in panel.Connections) { if (connection.Wires.Count > 0 || connection.CircuitBoxConnections.Count > 0) { return "Подключённое оборудование требует совместного преобразования проводов"; } }
            }
        }
        return null;
    }
    internal static void Validate(Item item, string value, bool simulation) {
        string reason = Unavailable(item); if (reason != null) { throw new InvalidOperationException(reason); }
        Point point = Parse(value);
        Vector2 center = new(point.X + item.Rect.Width * .5f, point.Y - item.Rect.Height * .5f);
        Hull destination = Hull.FindHull(center + item.Submarine.Position, useWorldCoordinates: true);
        if (destination?.Submarine != item.Submarine) { throw new InvalidOperationException("Точка должна находиться в помещении той же подлодки"); }
        if (!simulation) { return; }
        Rectangle destinationRect = new(point.X, point.Y, item.Rect.Width, item.Rect.Height);
        foreach (Character character in Character.CharacterList) {
            if (character.Removed || character.Submarine != item.Submarine) { continue; }
            Vector2 pos = character.WorldPosition - item.Submarine.Position;
            if (Submarine.RectContains(item.Rect, pos) || Submarine.RectContains(destinationRect, pos)) { throw new InvalidOperationException("В исходной или конечной области находится персонаж"); }
        }
    }
    internal static void Write(Item item, string value) {
        Point point = Parse(value); Vector2 amount = new(point.X - item.Rect.X, point.Y - item.Rect.Y);
        Submarine submarine = item.Submarine;
        item.Move(amount, ignoreContacts: true);
        // Native Move does not refresh static equipment hulls during a running round.
        item.FindHull();
        if (item.Submarine != submarine || item.Rect.X != point.X || item.Rect.Y != point.Y) { throw new InvalidOperationException("Нарушен результат преобразования оборудования"); }
    }
}
