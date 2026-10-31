using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma.Networking;

namespace PrimaryPackage.Core;

public static class XmlWire {
    public const int MaxStrings = 1024;
    public const int MaxAttributes = 64;
    public const int MaxChildren = 32;
    public const int MaxDepth = 4;

    public static void WriteElements(IWriteMessage msg, IReadOnlyList<XElement> elements, int count) {
        var order = new List<string>();
        var index = new Dictionary<string, int>();
        for (int i = 0; i < count; i++) { Collect(elements[i], order, index, 0); }

        msg.WriteVariableUInt32((uint)order.Count);
        for (int i = 0; i < order.Count; i++) { msg.WriteString(order[i]); }

        int max = order.Count - 1;
        for (int i = 0; i < count; i++) { Write(msg, elements[i], index, max, 0); }
    }

    public static bool ReadElements(IReadMessage msg, int count, out List<XElement> elements) {
        elements = null;
        uint tableCount = msg.ReadVariableUInt32();
        if (tableCount > MaxStrings) { return false; }
        var table = new string[tableCount];
        for (int i = 0; i < tableCount; i++) { table[i] = msg.ReadString(); }

        int max = (int)tableCount - 1;
        var read = new List<XElement>(count);
        for (int i = 0; i < count; i++) {
            if (!Read(msg, table, max, 0, out XElement element)) { return false; }
            read.Add(element);
        }
        elements = read;
        return true;
    }

    private static void Collect(XElement element, List<string> order, Dictionary<string, int> index, int depth) {
        if (element == null || depth > MaxDepth) { return; }
        Intern(element.Name.ToString(), order, index);
        foreach (XAttribute attribute in element.Attributes()) {
            Intern(attribute.Name.ToString(), order, index);
            Intern(attribute.Value, order, index);
        }
        bool hasChildren = false;
        foreach (XElement child in element.Elements()) {
            hasChildren = true;
            Collect(child, order, index, depth + 1);
        }
        if (!hasChildren && element.Value.Length > 0) { Intern(element.Value, order, index); }
    }

    private static void Intern(string value, List<string> order, Dictionary<string, int> index) {
        if (index.ContainsKey(value) || order.Count >= MaxStrings) { return; }
        index[value] = order.Count;
        order.Add(value);
    }

    private static void WriteIndex(IWriteMessage msg, string value, Dictionary<string, int> index, int max) {
        if (max <= 0) { return; }
        msg.WriteRangedInteger(index.TryGetValue(value, out int i) ? i : 0, 0, max);
    }

    private static void Write(IWriteMessage msg, XElement element, Dictionary<string, int> index, int max, int depth) {
        WriteIndex(msg, element.Name.ToString(), index, max);

        var attributes = new List<XAttribute>();
        foreach (XAttribute attribute in element.Attributes()) {
            if (attributes.Count >= MaxAttributes) { break; }
            attributes.Add(attribute);
        }
        msg.WriteByte((byte)attributes.Count);
        for (int i = 0; i < attributes.Count; i++) {
            WriteIndex(msg, attributes[i].Name.ToString(), index, max);
            WriteIndex(msg, attributes[i].Value, index, max);
        }

        var children = new List<XElement>();
        if (depth < MaxDepth) {
            foreach (XElement child in element.Elements()) {
                if (children.Count >= MaxChildren) { break; }
                children.Add(child);
            }
        }
        msg.WriteByte((byte)children.Count);
        for (int i = 0; i < children.Count; i++) { Write(msg, children[i], index, max, depth + 1); }

        bool hasText = children.Count == 0 && element.Value.Length > 0;
        msg.WriteBoolean(hasText);
        if (hasText) { WriteIndex(msg, element.Value, index, max); }
    }

    private static bool ReadIndex(IReadMessage msg, string[] table, int max, out string value) {
        value = null;
        if (table.Length == 0) { return false; }
        int i = max <= 0 ? 0 : msg.ReadRangedInteger(0, max);
        if (i < 0 || i >= table.Length) { return false; }
        value = table[i];
        return true;
    }

    private static bool Read(IReadMessage msg, string[] table, int max, int depth, out XElement element) {
        element = null;
        if (depth > MaxDepth) { return false; }
        if (!ReadIndex(msg, table, max, out string name)) { return false; }

        XElement built;
        try { built = new XElement(XName.Get(name)); }
        catch { return false; }

        int attributeCount = msg.ReadByte();
        if (attributeCount > MaxAttributes) { return false; }
        for (int i = 0; i < attributeCount; i++) {
            if (!ReadIndex(msg, table, max, out string attributeName)) { return false; }
            if (!ReadIndex(msg, table, max, out string attributeValue)) { return false; }
            try { built.SetAttributeValue(XName.Get(attributeName), attributeValue); }
            catch { return false; }
        }

        int childCount = msg.ReadByte();
        if (childCount > MaxChildren) { return false; }
        for (int i = 0; i < childCount; i++) {
            if (!Read(msg, table, max, depth + 1, out XElement child)) { return false; }
            built.Add(child);
        }

        if (msg.ReadBoolean()) {
            if (!ReadIndex(msg, table, max, out string text)) { return false; }
            built.Value = text;
        }

        element = built;
        return true;
    }
}
