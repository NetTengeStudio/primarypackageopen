using FarseerPhysics;
using FarseerPhysics.Collision;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using PrimaryPackage.Interactions;

internal static partial class Program {
    private static int checks;
    private static readonly AABB Bounds = new(new Vector2(-100), new Vector2(100));
    private static void Check(bool value, string name) { checks++; if (!value) { throw new InvalidOperationException(name); } }
    private static void Reject(Action action, string name) {
        bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } catch (ArgumentException) { rejected = true; }
        Check(rejected, name);
    }
    private static (World World, Body Body, Fixture Fixture) Circle() {
        var world = new World(Vector2.Zero); var body = new Body(); world.Add(body, false);
        var fixture = new Fixture(new CircleShape(1, 2), Category.Cat1, Category.All) { UserData = "owner", Friction = .7f, Restitution = .3f };
        body.Add(fixture); fixture.CollisionGroup = 2; return (world, body, fixture);
    }
    private static bool Ray(World world, Fixture target, Vector2 center) {
        bool found = false;
        world.RayCast((fixture, _, _, _) => { if (fixture == target) { found = true; return 0; } return 1; }, center - new Vector2(2, 0), center + new Vector2(2, 0));
        return found;
    }
    private static int Main() {
        try { Run(); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run() {
        var (world, body, fixture) = Circle(); var fixtures = new[] { fixture };
        FarseerPhysics.Dynamics.OnCollisionEventHandler callback = (_, _, _) => true; fixture.OnCollision = callback;
        var plan = StaticFixtureTranslation.Prepare(fixtures, body, new Vector2(5, 0), Bounds);
        Check(Ray(world, fixture, Vector2.Zero), "initial proxy exists");
        plan.Apply();
        Check(!Ray(world, fixture, Vector2.Zero) && Ray(world, fixture, new Vector2(5, 0)), "old collision disappears and new broad-phase proxy is queryable");
        Check(fixture.Body == body && body.FixtureList.Count == 1 && ReferenceEquals(fixture.OnCollision, callback), "fixture identity, owner and callbacks survive translation");
        Check(fixture.UserData as string == "owner" && fixture.Friction == .7f && fixture.Restitution == .3f && fixture.CollisionGroup == 2, "collision material, filters and userdata survive");
        plan.Revert();
        Check(Ray(world, fixture, Vector2.Zero) && !Ray(world, fixture, new Vector2(5, 0)), "revert removes destination proxies and restores source collision");
        Reject(() => plan.Apply(), "a reverted plan cannot be applied against a changed shape snapshot twice");
        var polygon = new Fixture(new PolygonShape(new Vertices { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) }, 1), Category.Cat2, Category.All);
        body.Add(polygon); var compound = new[] { fixture, polygon };
        var compoundPlan = StaticFixtureTranslation.Prepare(compound, body, new Vector2(7, 4), Bounds);
        compoundPlan.Apply();
        Check(Ray(world, polygon, new Vector2(7, 4)) && !Ray(world, polygon, Vector2.Zero), "polygon vertices and proxies move with compound geometry");
        compoundPlan.Revert(); Check(Ray(world, polygon, Vector2.Zero) && body.FixtureList.Count == 2, "compound inverse preserves fixture ownership");
        Reject(() => StaticFixtureTranslation.Validate(compound, body, new Vector2(float.NaN, 0), Bounds, false), "nonfinite delta is rejected before mutation");
        var small = new AABB(new Vector2(-2), new Vector2(2));
        Reject(() => StaticFixtureTranslation.Validate(compound, body, new Vector2(10, 0), small, false), "expanding submarine bounds is rejected");
        Reject(() => StaticFixtureTranslation.Validate(new[] { fixture, fixture }, body, new Vector2(1, 0), Bounds, false), "duplicate fixture ownership is rejected");
        var other = new Body { BodyType = BodyType.Dynamic, Position = new Vector2(10, 0) }; world.Add(other, false);
        other.Add(new Fixture(new CircleShape(1, 1), Category.Cat1, Category.All));
        Reject(() => StaticFixtureTranslation.Validate(fixtures, body, new Vector2(10, 0), Bounds, true), "occupied destination is rejected using real broad-phase queries");
        bool fault = true; var rollbackPlan = StaticFixtureTranslation.Prepare(compound, body, new Vector2(8, 0), Bounds);
        world.FixtureAdded = (_, _, _) => { if (fault) { fault = false; throw new InvalidOperationException("injected callback failure"); } };
        Reject(rollbackPlan.Apply, "injected attach failure is propagated");
        Check(body.FixtureList.Count == 2 && fixture.Body == body && polygon.Body == body && Ray(world, polygon, Vector2.Zero) && !Ray(world, polygon, new Vector2(8, 0)), "attach failure restores original shapes, ownership and broad phase");
        world.FixtureAdded = null;
        var (lockedWorld, lockedBody, lockedFixture) = Circle(); bool lockedRejected = false;
        var touching = new Body { BodyType = BodyType.Dynamic }; lockedWorld.Add(touching, false);
        touching.Add(new Fixture(new CircleShape(1, 1), Category.Cat1, Category.All));
        lockedFixture.OnCollision = (_, _, _) => {
            try { StaticFixtureTranslation.Validate(new[] { lockedFixture }, lockedBody, new Vector2(2, 0), Bounds, false); }
            catch (InvalidOperationException) { lockedRejected = true; }
            return true;
        };
        lockedWorld.Step(1f / 60);
        Check(lockedRejected, "operation is rejected inside an actual Farseer contact callback");
        Reject(() => StaticFixtureTranslation.Validate(new[] { lockedFixture }, lockedBody, new Vector2(5, 0), Bounds, true), "touching source contact is rejected outside physics step");
        var (movingWorld, movingBody, movingFixture) = Circle();
        movingBody.BodyType = BodyType.Dynamic; movingBody.Position = new Vector2(11, -7); movingBody.Rotation = .5f;
        movingBody.LinearVelocity = new Vector2(5, -3); movingBody.AngularVelocity = .7f;
        Vector2 originalVelocity = movingBody.GetLinearVelocityFromLocalPoint(Vector2.Zero);
        Vector2 originalPosition = movingBody.Position; float originalMass = movingBody.Mass;
        var movingPlan = StaticFixtureTranslation.Prepare(new[] { movingFixture }, movingBody, new Vector2(4, 2), Bounds);
        movingPlan.Apply();
        Check(Ray(movingWorld, movingFixture, movingBody.GetWorldPoint(new Vector2(4, 2))), "local translation uses the rotated moving body's actual world transform");
        Check(Vector2.Distance(originalVelocity, movingBody.GetLinearVelocityFromLocalPoint(Vector2.Zero)) < .00001f && movingBody.AngularVelocity == .7f, "fixture relocation preserves velocity at the body's origin");
        Check(movingBody.Position == originalPosition && Math.Abs(movingBody.Mass - originalMass) < .00001f, "translation preserves body pose and total mass");
        movingPlan.Revert();
        Check(movingBody.LinearVelocity == new Vector2(5, -3) && movingBody.AngularVelocity == .7f, "immediate rollback restores original kinematics");
        int fixtureChecks = checks; TransformPolicyContracts();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { status = "PASS", checks, fixtureChecks, policyChecks = checks - fixtureChecks,
            physicsAssembly = typeof(World).Assembly.ManifestModule.ModuleVersionId,
            scope = "fixtureChecks use production translation with installed Farseer world/contacts/shapes/broad phase; policyChecks use production LiveItemTransform with engine decision stubs. No native game objects, Barotrauma startup, networking or GPU" }));
    }
}
