using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    
    internal static class WallMovementQueries {
        private sealed class QueryState {
            internal readonly Ragdoll Ragdoll;
            internal readonly Predicate<Fixture> Keep;
            internal readonly Func<Fixture, Vector2, Vector2, float, float> Floor;
            internal Predicate<Fixture> OriginalPredicate;
            internal Func<Fixture, Vector2, Vector2, float, float> OriginalFloor;

            internal QueryState(Ragdoll ragdoll) {
                Ragdoll = ragdoll;
                Keep = KeepFixture;
                Floor = FloorHit;
            }

            private bool KeepFixture(Fixture fixture) =>
                (OriginalPredicate == null || OriginalPredicate(fixture)) &&
                !WallContacts.Passable(fixture, Ragdoll.Collider?.FarseerBody);

            private float FloorHit(Fixture fixture, Vector2 point, Vector2 normal, float fraction) =>
                WallContacts.Passable(fixture, Ragdoll.Collider?.FarseerBody)
                    ? -1f : OriginalFloor(fixture, point, normal, fraction);
        }

        private static readonly ConditionalWeakTable<Ragdoll, QueryState> states = new ConditionalWeakTable<Ragdoll, QueryState>();

        internal static void Attach(Harmony harmony) {
            foreach (string name in new[] { "CheckDistFromCollider", "TrySetLimbPosition", "GetFloorY" }) {
                MethodInfo target = AccessTools.Method(typeof(Ragdoll), name);
                if (target == null) { throw new MissingMethodException("Ragdoll." + name); }
                harmony.Patch(target, transpiler: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallMovementQueries), nameof(Transpile)));
            }
        }

        internal static void Release() => states.Clear();

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions) {
            var result = new List<CodeInstruction>();
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions) {
                MethodInfo replacement = null;
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                    instruction.operand is MethodInfo method) {
                    if (method.DeclaringType == typeof(Submarine) && method.Name == nameof(Submarine.PickBody)) {
                        replacement = AccessTools.Method(typeof(WallMovementQueries), nameof(PickBody));
                    }
                    else if (method.DeclaringType == typeof(World) && method.Name == nameof(World.RayCast)) {
                        replacement = AccessTools.Method(typeof(WallMovementQueries), nameof(FloorRay));
                    }
                    if (replacement != null) {
                        Validate(method, replacement);
                        result.Add(new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction));
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                        replaced++;
                    }
                }
                result.Add(instruction);
            }
            if (replaced != 1) { throw new InvalidOperationException("Expected one movement ray, found " + replaced); }
            return result;
        }

        private static void Validate(MethodInfo original, MethodInfo replacement) {
            ParameterInfo[] before = original.GetParameters();
            ParameterInfo[] after = replacement.GetParameters();
            int receiver = original.IsStatic ? 0 : 1;
            if (after.Length != before.Length + receiver + 1 || original.ReturnType != replacement.ReturnType ||
                (receiver != 0 && after[0].ParameterType != original.DeclaringType)) {
                throw new InvalidOperationException("Movement ray signature changed: " + original);
            }
            for (int i = 0; i < before.Length; i++) {
                if (before[i].ParameterType != after[i + receiver].ParameterType) {
                    throw new InvalidOperationException("Movement ray parameter changed: " + original);
                }
            }
        }

        public static Body PickBody(Vector2 start, Vector2 end, IEnumerable<Body> ignoredBodies,
            Category? category, bool ignoreSensors, Predicate<Fixture> predicate, bool allowInside, Ragdoll ragdoll) {
            if (WallContacts.Count == 0) { return Submarine.PickBody(start, end, ignoredBodies, category, ignoreSensors, predicate, allowInside); }
            QueryState state = states.GetValue(ragdoll, static key => new QueryState(key));
            
            lock (state) {
                Predicate<Fixture> previous = state.OriginalPredicate;
                state.OriginalPredicate = predicate;
                try { return Submarine.PickBody(start, end, ignoredBodies, category, ignoreSensors, state.Keep, allowInside); }
                finally { state.OriginalPredicate = previous; }
            }
        }

        public static void FloorRay(World world, Func<Fixture, Vector2, Vector2, float, float> callback,
            Vector2 start, Vector2 end, Category category, Ragdoll ragdoll) {
            if (WallContacts.Count == 0) { world.RayCast(callback, start, end, category); return; }
            QueryState state = states.GetValue(ragdoll, static key => new QueryState(key));
            lock (state) {
                var previous = state.OriginalFloor;
                state.OriginalFloor = callback;
                try { world.RayCast(state.Floor, start, end, category); }
                finally { state.OriginalFloor = previous; }
            }
        }
    }
}
