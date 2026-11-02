// These adapters exercise production admission/projection decisions, not native Item.Move.
// Fixtures/contacts in Program.cs use the actual installed Farseer implementation.
using Microsoft.Xna.Framework;
using PrimaryPackage.Interactions;

namespace Barotrauma {
    internal static class GameMain { internal static FarseerPhysics.Dynamics.World World; }
    internal sealed class PhysicsBody { internal bool Enabled { get; set; } internal FarseerPhysics.Dynamics.Body FarseerBody { get; set; } }
    internal sealed class SubmarineBody { internal PhysicsBody Body { get; set; } }
    internal sealed class Submarine {
        internal bool Removed { get; set; } internal bool FlippedX { get; set; }
        internal Vector2 Position { get; set; }
        internal Rectangle Borders { get; set; }
        internal SubmarineBody SubBody { get; set; }
        internal static bool RectContains(Rectangle rect, Vector2 point) => point.X >= rect.X && point.X <= rect.Right && point.Y <= rect.Y && point.Y >= rect.Y - rect.Height;
    }
    internal sealed class Hull {
        internal Submarine Submarine { get; set; }
        internal static Hull Destination;
        internal static Hull FindHull(Vector2 center, bool useWorldCoordinates) => Destination;
    }
    internal sealed class Character {
        internal static readonly List<Character> CharacterList = new();
        internal bool Removed { get; set; }
        internal Submarine Submarine { get; set; }
        internal Vector2 WorldPosition { get; set; }
    }
    internal sealed class Item {
        internal bool Removed { get; set; }
        internal object ParentInventory { get; set; } internal object StaticBodyConfig { get; set; }
        internal Submarine Submarine { get; set; }
        internal readonly List<FarseerPhysics.Dynamics.Fixture> StaticFixtures = new();
        internal PhysicsBody body { get; set; }
        internal readonly List<Items.Components.ItemComponent> Components = new();
        internal Rectangle Rect;
        internal void Move(Vector2 amount, bool ignoreContacts) { Rect = new(Rect.X + (int)amount.X, Rect.Y + (int)amount.Y, Rect.Width, Rect.Height); }
        internal void FindHull() { }
    }
}
namespace Barotrauma.Items.Components {
    internal class ItemComponent { }
    internal sealed class Door : ItemComponent { }
    internal sealed class DockingPort : ItemComponent { }
    internal sealed class Wire : ItemComponent { }
    internal sealed class ItemContainer : ItemComponent { }
    internal sealed class Holdable : ItemComponent { }
    internal sealed class ConnectionPanel : ItemComponent {
        internal readonly List<Wire> DisconnectedWires = new();
        internal readonly List<Connection> Connections = new();
    }
    internal sealed class Connection {
        internal readonly List<Wire> Wires = new();
        internal readonly List<object> CircuitBoxConnections = new();
    }
}
internal static partial class Program {
    private static void TransformPolicyContracts() {
        Barotrauma.GameMain.World = new FarseerPhysics.Dynamics.World(Vector2.Zero);
        var submarine = new Barotrauma.Submarine();
        Barotrauma.Hull.Destination = new() { Submarine = submarine };
        var item = new Barotrauma.Item { Submarine = submarine, Rect = new Rectangle(0, 0, 10, 10) };
        var panel = new Barotrauma.Items.Components.ConnectionPanel(); var connection = new Barotrauma.Items.Components.Connection();
        connection.Wires.Add(new()); panel.Connections.Add(connection); item.Components.Add(panel);
        Reject(() => LiveItemTransform.Validate(item, "10,0", true), "server rejects a new move of connected equipment");
        LiveItemTransform.Validate(item, "10,0", false);
        Check(connection.Wires.Count == 1, "authoritative projection tolerates newer native wiring without modifying it");
        connection.Wires.Clear(); panel.DisconnectedWires.Add(new());
        Reject(() => LiveItemTransform.Validate(item, "10,0", true), "server rejects loose wires");
        LiveItemTransform.Validate(item, "10,0", false);
        Check(panel.DisconnectedWires.Count == 1, "projection preserves native loose-wire ownership");
        panel.DisconnectedWires.Clear(); connection.CircuitBoxConnections.Add(new());
        Reject(() => LiveItemTransform.Validate(item, "10,0", true), "server rejects circuit-box links");
        LiveItemTransform.Validate(item, "10,0", false);
        Check(connection.CircuitBoxConnections.Count == 1, "projection does not create or remove circuit-box links");
        item.Components.Add(new Barotrauma.Items.Components.Holdable());
        Reject(() => LiveItemTransform.Validate(item, "10,0", true), "attachment/body/inventory transition requires a specialized authority contract");
        Reject(() => LiveItemTransform.Validate(item, "10,0", false), "unsupported attachment geometry is not treated as a simple projection");
        item.Components.Clear(); item.Submarine = null;
        Reject(() => LiveItemTransform.Validate(item, "10,0", false), "projection still requires an actual submarine frame");
        item.Submarine = submarine; Barotrauma.Hull.Destination = new() { Submarine = new() };
        Reject(() => LiveItemTransform.Validate(item, "10,0", false), "projection cannot silently migrate an entity to another submarine");
    }
}
