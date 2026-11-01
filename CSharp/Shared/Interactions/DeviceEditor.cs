using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Interactions;

internal enum DeviceValueKind : byte { Boolean, Integer, Number, Text, Identifier, Enumeration, Color, Vector2 }

/// <summary>Metadata discovery is separate from the policy that authorizes setters.</summary>
internal static class DeviceEditor {
    internal sealed class Field {
        internal readonly SerializableProperty Property;
        internal readonly DeviceValueKind Kind;
        internal readonly Editable Metadata;
        internal string Name => Property.Name;
        internal string Label {
            get {
                string tag = Property.PropertyInfo.DeclaringType.Name + "." + Name;
                LocalizedString label = TextManager.Get(tag.ToIdentifier(), ("sp." + tag + ".name").ToIdentifier());
                if (label.IsNullOrEmpty()) { label = TextManager.Get(string.IsNullOrEmpty(Metadata.FallBackTextTag) ? Name : Metadata.FallBackTextTag); }
                return label.IsNullOrEmpty() ? Name : label.Value;
            }
        }
        internal string[] Choices => Kind == DeviceValueKind.Enumeration ? Enum.GetNames(Property.PropertyType) : Array.Empty<string>();
        internal Field(SerializableProperty property, DeviceValueKind kind, Editable metadata) { Property = property; Kind = kind; Metadata = metadata; }
        private void Set(object target, object value) {
            if (Property.PropertyInfo.DeclaringType == typeof(Door) && Name == nameof(Door.IsOpen)) {
                ((Door)target).TrySetState((bool)value, isNetworkMessage: true, sendNetworkMessage: GameMain.NetworkMember is { IsServer: true });
            } else { Property.PropertyInfo.SetValue(target, value); }
        }
        internal string Read(object target) => Format(Property.PropertyInfo.GetValue(target));
        internal object Parse(string text) {
            if (text == null || text.Length > 512) { throw new ArgumentException("Значение слишком длинное"); }
            Type type = Property.PropertyType;
            switch (Kind) {
                case DeviceValueKind.Boolean: return text switch { "true" => true, "false" => false, _ => throw new ArgumentException("Ожидается true или false") };
                case DeviceValueKind.Integer:
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer) && integer >= Metadata.MinValueInt && integer <= Metadata.MaxValueInt) { return integer; }
                    break;
                case DeviceValueKind.Number:
                    if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number) && number >= Metadata.MinValueFloat && number <= Metadata.MaxValueFloat) { return number; }
                    break;
                case DeviceValueKind.Text:
                case DeviceValueKind.Identifier:
                    if (Metadata.MaxLength > 0 && text.Length > Metadata.MaxLength) { break; }
                    foreach (char c in text) { if (char.IsControl(c) || c is '<' or '>') { throw new ArgumentException("Управляющие символы и разметка запрещены"); } }
                    if (Kind == DeviceValueKind.Text) { return text; }
                    if (text.Length > 128) { break; }
                    return text.ToIdentifier();
                case DeviceValueKind.Enumeration:
                    if (Array.IndexOf(Enum.GetNames(type), text) >= 0) { return Enum.Parse(type, text, false); }
                    break;
                case DeviceValueKind.Color:
                    Span<float> channels = stackalloc float[4];
                    if (Parts(text, channels, true)) { return new Color((byte)channels[0], (byte)channels[1], (byte)channels[2], (byte)channels[3]); }
                    break;
                case DeviceValueKind.Vector2:
                    Span<float> coordinates = stackalloc float[2];
                    if (Parts(text, coordinates, false) && coordinates[0] >= Metadata.MinValueFloat && coordinates[0] <= Metadata.MaxValueFloat && coordinates[1] >= Metadata.MinValueFloat && coordinates[1] <= Metadata.MaxValueFloat) { return new Vector2(coordinates[0], coordinates[1]); }
                    break;
            }
            throw new ArgumentException("Неверный тип или значение вне диапазона " + Name);
        }
        internal void Write(object target, string text) {
            object value = Parse(text), before = Property.PropertyInfo.GetValue(target);
            try {
                Set(target, value);
                if (!Equals(Property.PropertyInfo.GetValue(target), value)) { throw new InvalidOperationException("Компонент отклонил значение " + Name); }
            } catch (Exception error) {
                try { Set(target, before); }
                catch (Exception rollback) { throw new AggregateException("Не удалось восстановить настройку " + Name, error, rollback); }
                throw new InvalidOperationException("Правка " + Name + " отклонена: " + (error.InnerException?.Message ?? error.Message), error);
            }
        }
        internal string Range => Kind == DeviceValueKind.Integer ? Metadata.MinValueInt + " … " + Metadata.MaxValueInt :
            Kind == DeviceValueKind.Number || Kind == DeviceValueKind.Vector2 ? Format(Metadata.MinValueFloat) + " … " + Format(Metadata.MaxValueFloat) :
            Kind == DeviceValueKind.Color ? "R,G,B,A · 0 … 255" : "До " + (Metadata.MaxLength > 0 ? Math.Min(512, Metadata.MaxLength) : 512) + " символов";
    }
    private static readonly Dictionary<Type, Field[]> schemas = new();
    private static readonly HashSet<Type> registered = new();
    // A mod registers a capability once per component type, never once per property.
    internal static void Register<T>() where T : ItemComponent { registered.Add(typeof(T)); schemas.Remove(typeof(T)); }
    private static bool Permitted(Type type, SerializableProperty property, Editable metadata) {
        if (metadata == null || metadata.ReadOnly || property.GetAttribute<Serialize>() == null ||
            property.PropertyInfo?.GetMethod?.IsPublic != true || property.PropertyInfo.SetMethod?.IsPublic != true ||
            property.PropertyInfo.GetIndexParameters().Length != 0) { return false; }
        bool native = type.Assembly == typeof(ItemComponent).Assembly;
        if (type == typeof(Door) && property.Name == nameof(Door.IsOpen)) { return true; }
        if (!(metadata is InGameEditable) || !native && !registered.Contains(type)) { return false; }
        // Physical/topological owners require operation-specific adapters.
        return !typeof(Wire).IsAssignableFrom(type) && !typeof(ConnectionPanel).IsAssignableFrom(type) &&
            !typeof(DockingPort).IsAssignableFrom(type) && !typeof(Holdable).IsAssignableFrom(type);
    }
    internal static Field[] Schema(ItemComponent component) => Schema(component.GetType(), component.SerializableProperties);
    internal static Field[] Schema(Type type, IReadOnlyDictionary<Identifier, SerializableProperty> properties) {
        if (schemas.TryGetValue(type, out Field[] result)) { return result; }
        var fields = new List<Field>();
        foreach (SerializableProperty property in properties.Values) {
            Editable metadata = property.GetAttribute<Editable>();
            if (!Permitted(type, property, metadata) || !Kind(property.PropertyType, out DeviceValueKind kind)) { continue; }
            fields.Add(new Field(property, kind, metadata));
        }
        fields.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return schemas[type] = fields.ToArray();
    }
    internal static bool Kind(Type type, out DeviceValueKind kind) {
        if (type == typeof(bool)) { kind = DeviceValueKind.Boolean; }
        else if (type == typeof(int)) { kind = DeviceValueKind.Integer; }
        else if (type == typeof(float)) { kind = DeviceValueKind.Number; }
        else if (type == typeof(string)) { kind = DeviceValueKind.Text; }
        else if (type == typeof(Identifier)) { kind = DeviceValueKind.Identifier; }
        else if (type.IsEnum) { kind = DeviceValueKind.Enumeration; }
        else if (type == typeof(Color)) { kind = DeviceValueKind.Color; }
        else if (type == typeof(Vector2)) { kind = DeviceValueKind.Vector2; }
        else { kind = default; return false; }
        return true;
    }
    internal static ItemComponent Resolve(Item item, ushort component, string type) {
        if (item is not { Removed: false } || component >= item.Components.Count || item.Components[component].GetType().FullName != type) { throw new ArgumentException("Устройство или экземпляр компонента изменились"); }
        return item.Components[component];
    }
    internal static Field Resolve(ItemComponent target, string property) {
        foreach (Field field in Schema(target)) { if (field.Name == property) { return field; } }
        throw new ArgumentException("Свойство не разрешено политикой Live Editor: " + property);
    }
    internal static string Format(object value) => value switch {
        bool flag => flag ? "true" : "false", float number => number.ToString("R", CultureInfo.InvariantCulture),
        Color color => $"{color.R},{color.G},{color.B},{color.A}", Vector2 vector => Format(vector.X) + "," + Format(vector.Y),
        Identifier identifier => identifier.Value, IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture), _ => value?.ToString() ?? ""
    };
    private static bool Parts(string text, Span<float> parts, bool bytes) {
        ReadOnlySpan<char> rest = text.AsSpan();
        for (int i = 0; i < parts.Length; i++) {
            int comma = rest.IndexOf(',');
            if ((i == parts.Length - 1) != (comma < 0)) { return false; }
            ReadOnlySpan<char> part = comma < 0 ? rest : rest[..comma];
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || bytes && (value < 0 || value > 255 || value != MathF.Truncate(value))) { return false; }
            parts[i] = value; rest = comma < 0 ? default : rest[(comma + 1)..];
        }
        return true;
    }
}

// Extensions opt a component type into the same schema policy during initialization.
// This does not authorize topology, private fields, missing metadata or unsupported value types.
public static class LiveComponentCapabilities {
    public static void Register<T>() where T : ItemComponent {
        if (GameMain.GameSession?.IsRunning == true) { throw new InvalidOperationException("Регистрируйте возможности Live Editor до запуска раунда"); }
        DeviceEditor.Register<T>();
    }
}
