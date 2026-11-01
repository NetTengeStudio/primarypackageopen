using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;
using Barotrauma.Networking;
using FarseerPhysics;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Mining {
    public partial class DrillComponent : ItemComponent, IServerSerializable {
        private RepairTool tool;
        private Powered powered;

        [Serialize(true, IsPropertySaveable.Yes)] public bool State { get; set; }
        [Serialize(8, IsPropertySaveable.No)] public int FrameCount { get; set; }
        [Serialize(55.0f, IsPropertySaveable.No)] public float ThreadPeriod { get; set; }
        [Serialize(0.29f, IsPropertySaveable.No)] public float ThreadSlope { get; set; }
        [Serialize(88, IsPropertySaveable.No)] public int ScrollStart { get; set; }
        [Serialize(4.5f, IsPropertySaveable.No)] public float RotationSpeed { get; set; }
        [Serialize(1.5f, IsPropertySaveable.No)] public float VibrationAmount { get; set; }
        [Serialize(115.0f, IsPropertySaveable.No)] public float BitLength { get; set; }
        [Serialize(170.0f, IsPropertySaveable.No)] public float BitWidth { get; set; }
        [Serialize(450.0f, IsPropertySaveable.No)] public float ShakeRange { get; set; }
        [Serialize(1.5f, IsPropertySaveable.No)] public float ShakeStrength { get; set; }
        [Serialize(15.0f, IsPropertySaveable.No)] public float SpinUpDuration { get; set; }
        [Serialize(3.0f, IsPropertySaveable.No)] public float SpinDownDuration { get; set; }
        [Serialize(0.01f, IsPropertySaveable.No)] public float IdleHeatRate { get; set; }
        [Serialize(0.055f, IsPropertySaveable.No)] public float DrillHeatRate { get; set; }
        [Serialize(0.015f, IsPropertySaveable.No)] public float CoolRate { get; set; }
        [Serialize(2.5f, IsPropertySaveable.No)] public float WaterCoolMultiplier { get; set; }
        [Serialize(0.5f, IsPropertySaveable.No)] public float OverheatRecoverAt { get; set; }
        [Serialize(1.5f, IsPropertySaveable.No)] public float OverheatConditionDamage { get; set; }
        [Serialize(0.45f, IsPropertySaveable.No)] public float OreChance { get; set; }

        [Header("Damage")]
        [Editable(MinValueFloat = 0f, MaxValueFloat = 500f, DecimalCount = 1), Serialize(25.0f, IsPropertySaveable.Yes, description: "Damage per second dealt to structures and destructible level walls inside the wall damage zone.")]
        public float WallDamage { get; set; }
        [Editable(MinValueFloat = 0f, MaxValueFloat = 500f, DecimalCount = 1), Serialize(8.0f, IsPropertySaveable.Yes, description: "Damage per second dealt to characters inside the character damage zone.")]
        public float CharacterDamage { get; set; }

        [Header("Aim")]
        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Разворачивает ось бура на 180 градусов, если бур направлен в противоположную сторону.")]
        public bool ReverseAxis { get; set; }

        [Header("Wall damage zone")]
        [Editable(MinValueFloat = 0f, MaxValueFloat = 3000f, ValueStep = 10f), Serialize(220.0f, IsPropertySaveable.Yes, description: "How far along the drill's axis the wall damage zone extends.")]
        public float WallZoneLength { get; set; }
        [Editable(MinValueFloat = 0f, MaxValueFloat = 1000f, ValueStep = 10f), Serialize(40.0f, IsPropertySaveable.Yes, description: "Width of the wall damage zone.")]
        public float WallZoneWidth { get; set; }

        [Header("Character damage zone")]
        [Editable(MinValueFloat = 0f, MaxValueFloat = 3000f, ValueStep = 10f), Serialize(115.0f, IsPropertySaveable.Yes, description: "How far along the drill's axis the character damage zone extends.")]
        public float CharacterZoneLength { get; set; }
        [Editable(MinValueFloat = 0f, MaxValueFloat = 1000f, ValueStep = 10f), Serialize(170.0f, IsPropertySaveable.Yes, description: "Width of the character damage zone.")]
        public float CharacterZoneWidth { get; set; }

        public bool IsRunning { get; private set; }
        public float Intensity { get; private set; }
        public float Heat { get; private set; }
        public bool Overheated { get; private set; }
        public override bool UpdateWhenInactive => true;

        private const float CheckInterval = 1.0f / 12.0f;
        private const float SyncInterval = 3.0f;
        private float checkTimer;
        private float syncTimer;

        private FarseerPhysics.Dynamics.Body cachedCellBody;
        private DestructibleLevelWall cachedLevelWall;
        private FarseerPhysics.Dynamics.Body lookupCellBody;
        private readonly Predicate<LevelWall> cellBodyMatch;

        private readonly List<(Identifier id, float commonness)> oreTable = new List<(Identifier, float)>();
        private float oreTotalCommonness;

        public DrillComponent(Item item, ContentXElement element) : base(item, element) {
            cellBodyMatch = w => w.Body == lookupCellBody;
            foreach (var subElement in element.Elements()) {
                if (!subElement.Name.ToString().Equals("ore", StringComparison.OrdinalIgnoreCase)) { continue; }
                Identifier id = subElement.GetAttributeIdentifier("identifier", Identifier.Empty);
                float commonness = subElement.GetAttributeFloat("commonness", 1.0f);
                if (id.IsEmpty || commonness <= 0.0f) { continue; }
                oreTable.Add((id, commonness));
                oreTotalCommonness += commonness;
            }
            InitProjSpecific(element);
        }

        partial void InitProjSpecific(ContentXElement element);
        partial void UpdateProjSpecific(float deltaTime);
        partial void OnStateChanged();
        partial void RequestSync();
        partial void DrillHitProjSpecific(bool hit, Vector2 hitWorldPos, Vector2 axis, float deltaTime);
        partial void OnBreakProjSpecific();

        private bool wasBroken;

        public override void OnItemLoaded() {
            tool = item.GetComponent<RepairTool>();
            powered = item.GetComponent<Powered>();
            wasBroken = item.Condition <= 0.0f;
        }

        public override void Update(float deltaTime, Camera cam) {
            if (powered != null) { powered.IsActive = State; }
            IsRunning = State && item.Condition > 0.0f && powered != null && powered.Voltage >= powered.MinVoltage;
            IsActive = IsRunning;

            UpdateIntensityAndHeat(deltaTime);

            if (!IsRunning) {
                checkTimer = 0.0f;
            } else if ((checkTimer += deltaTime) >= CheckInterval) {
                float acc = checkTimer;
                checkTimer = 0.0f;
                DoDrill(acc);
            }

            if ((IsRunning || Heat > 0.0f) && (syncTimer += deltaTime) >= SyncInterval) {
                syncTimer = 0.0f;
                RequestSync();
            }

            UpdateProjSpecific(deltaTime);
        }

        public override void UpdateBroken(float deltaTime, Camera cam) {
            IsRunning = false;
            IsActive = false;
            UpdateIntensityAndHeat(deltaTime);
            if (Heat > 0.0f && (syncTimer += deltaTime) >= SyncInterval) {
                syncTimer = 0.0f;
                RequestSync();
            }
            UpdateProjSpecific(deltaTime);
            base.UpdateBroken(deltaTime, cam);
        }

        private void UpdateIntensityAndHeat(float deltaTime) {
            float target = IsRunning ? 1.0f : 0.0f;
            float rate = (IsRunning ? 1.0f / SpinUpDuration : 1.0f / SpinDownDuration) * deltaTime;
            Intensity = Math.Abs(target - Intensity) <= rate ? target : Intensity + Math.Sign(target - Intensity) * rate;

            float heat = Heat;
            if (IsRunning) { heat += IdleHeatRate * Intensity * deltaTime; }
            heat -= CoolRate * (IsTipInWater() ? WaterCoolMultiplier : 1.0f) * deltaTime;
            Heat = MathHelper.Clamp(heat, 0.0f, 1.0f);

            if (!Overheated && Heat >= 1.0f) {
                Overheated = true;
                RequestSync();
            } else if (Overheated && Heat <= OverheatRecoverAt) {
                Overheated = false;
                RequestSync();
            }

            if (Overheated && OverheatConditionDamage > 0.0f &&
                (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient)) {
                item.Condition -= OverheatConditionDamage * deltaTime;
            }

            bool broken = item.Condition <= 0.0f;
            if (broken != wasBroken) {
                wasBroken = broken;
                if (broken && Overheated) { OnBreakProjSpecific(); }
            }
        }

        private RepairTool Tool => tool ??= item.GetComponent<RepairTool>();

        public Vector2 DrillAxis() {
            Vector2 dir;
            if (item.body != null && item.body.BodyType != BodyType.Static) {
                float aim = MathHelper.ToRadians(Tool?.BarrelRotation ?? 0.0f) + item.body.Rotation;
                dir = new Vector2((float)Math.Cos(aim), (float)Math.Sin(aim)) * item.body.Dir;
            }
            else {
                float angle = -item.RotationRad + MathHelper.ToRadians(Tool?.BarrelRotation ?? 0.0f);
                dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                if (item.FlippedX) { dir = -dir; }
            }
            return ReverseAxis ? -dir : dir;
        }

        public Vector2 DrillOrigin() {
            Vector2 barrel = Tool?.BarrelPos ?? Vector2.Zero;
            if (barrel == Vector2.Zero) { return item.WorldPosition; }
            Vector2 axis = DrillAxis();
            Vector2 perp = new Vector2(-axis.Y, axis.X);
            return item.WorldPosition + axis * barrel.X + perp * barrel.Y;
        }

        public Vector2 TipWorldPosition() => DrillOrigin() + DrillAxis() * BitLength;

        public bool IsTipInWater() {
            Vector2 tip = TipWorldPosition();
            Hull hull = Hull.FindHull(tip, item.CurrentHull);
            return hull == null || tip.Y < hull.WorldSurface;
        }

        public static bool IsInZone(Vector2 worldPos, Vector2 itemWorldPos, Vector2 axis, float length, float width) {
            Vector2 rel = worldPos - itemWorldPos;
            float along = Vector2.Dot(rel, axis);
            if (along < 0.0f || along > length) { return false; }
            Vector2 perp = new Vector2(-axis.Y, axis.X);
            return Math.Abs(Vector2.Dot(rel, perp)) <= width * 0.5f;
        }

        public Vector2[] GetZoneCorners(float length, float width) {
            Vector2 axis = DrillAxis();
            Vector2 perp = new Vector2(-axis.Y, axis.X) * (width * 0.5f);
            Vector2 origin = DrillOrigin();
            Vector2 far = origin + axis * length;
            return new[] { origin + perp, far + perp, far - perp, origin - perp };
        }

        private readonly FarseerPhysics.Dynamics.Body[] ignoredBodyBuffer = new FarseerPhysics.Dynamics.Body[1];
        private static readonly float[] RayOffsets = { 0.0f, -0.5f, 0.5f };

        private static readonly Category DrillCategories =
            Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionRepairableWall;

        private void DoDrill(float deltaTime) {
            deltaTime *= Intensity;
            float length = WallZoneLength;
            if (deltaTime <= 0.0f || length <= 0.0f) { return; }

            Vector2 axis = DrillAxis();
            Vector2 origin = DrillOrigin();
            Vector2 spread = new Vector2(-axis.Y, axis.X) * WallZoneWidth;
            int rayCount = WallZoneWidth > 0.0f ? RayOffsets.Length : 1;
            float damage = WallDamage * deltaTime / rayCount;

            ignoredBodyBuffer[0] = item.body?.FarseerBody;
            Character user = item.ParentInventory?.Owner as Character;
            Submarine parentSub = user?.Submarine ?? item.Submarine;

            bool hit = false;
            float nearest = float.MaxValue;
            Vector2 hitWorldPos = origin + axis * length;
            float cullRadius = length + WallZoneWidth;

            for (int r = 0; r < rayCount; r++) {
                Vector2 rayOrigin = origin + spread * RayOffsets[r];
                Vector2 start = ConvertUnits.ToSimUnits(rayOrigin);
                Vector2 end = ConvertUnits.ToSimUnits(rayOrigin + axis * length);
                if (parentSub == null) {
                    foreach (Submarine sub in Submarine.Loaded) {
                        Rectangle subBorders = sub.Borders;
                        subBorders.Location += new Point((int)sub.WorldPosition.X,
                            (int)sub.WorldPosition.Y - sub.Borders.Height);
                        if (!MathUtils.CircleIntersectsRectangle(rayOrigin, cullRadius, subBorders)) { continue; }
                        Cast(start - sub.SimPosition, end - sub.SimPosition, sub, damage, deltaTime / rayCount,
                            origin, axis, ref hit, ref nearest, ref hitWorldPos);
                    }
                    Cast(start, end, null, damage, deltaTime / rayCount,
                        origin, axis, ref hit, ref nearest, ref hitWorldPos);
                }
                else {
                    Cast(start - parentSub.SimPosition, end - parentSub.SimPosition, parentSub, damage,
                        deltaTime / rayCount, origin, axis, ref hit, ref nearest, ref hitWorldPos);
                }
            }

            if (hit) {
                Heat = Math.Min(1.0f, Heat + DrillHeatRate * deltaTime);
            }

            HitCharactersInBitZone(deltaTime, axis);
            DrillHitProjSpecific(hit, hitWorldPos, axis, deltaTime);
        }

        private void Cast(Vector2 start, Vector2 end, Submarine sub, float damage, float deltaTime,
            Vector2 origin, Vector2 axis, ref bool hit, ref float nearest, ref Vector2 hitWorldPos) {
            var body = Submarine.PickBody(start, end, ignoredBodyBuffer[0] == null ? null : ignoredBodyBuffer,
                DrillCategories, allowInsideFixture: true);
            if (body == null) { return; }
            Vector2 hitPos = ConvertUnits.ToDisplayUnits(Submarine.LastPickedPosition);
            if (sub != null) { hitPos += sub.Position; }

            if (body.UserData is Voronoi2.VoronoiCell cell && cell.IsDestructible) {
                DestructibleLevelWall wall = ResolveLevelWall(cell.Body);
                if (wall == null) { return; }
                wall.AddDamage(damage, hitPos);
                if (wall.Destroyed) {
                    cachedCellBody = null;
                    cachedLevelWall = null;
                    TrySpawnOre(hitPos, axis);
                }
            }
            else if (body.UserData is LevelObject levelObject && levelObject.Prefab.TakeLevelWallDamage) {
                levelObject.AddDamage(damage, deltaTime, item);
            }
            else if (body.UserData is Structure structure && !structure.IsPlatform) {
                int sectionIndex = structure.FindSectionIndex(ConvertUnits.ToDisplayUnits(Submarine.LastPickedPosition));
                if (sectionIndex < 0) { return; }
                structure.AddDamage(sectionIndex, damage, null, emitParticles: false);
            }
            else { return; }

            hit = true;
            TrackNearest(hitPos, origin, axis, ref nearest, ref hitWorldPos);
        }

        private static void TrackNearest(Vector2 pos, Vector2 origin, Vector2 axis, ref float nearest, ref Vector2 best) {
            float along = Vector2.Dot(pos - origin, axis);
            if (along >= nearest) { return; }
            nearest = along;
            best = pos;
        }

        private void TrySpawnOre(Vector2 hitWorldPos, Vector2 axis) {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (oreTable.Count == 0 || Entity.Spawner == null) { return; }
            if (Rand.Range(0.0f, 1.0f) > OreChance) { return; }
            float r = Rand.Range(0.0f, oreTotalCommonness);
            foreach (var (id, commonness) in oreTable) {
                r -= commonness;
                if (r > 0.0f) { continue; }
                if (ItemPrefab.Prefabs.TryGet(id, out ItemPrefab prefab)) {
                    Entity.Spawner.AddItemToSpawnQueue(prefab, hitWorldPos - axis * 30.0f);
                }
                return;
            }
        }

        private DestructibleLevelWall ResolveLevelWall(FarseerPhysics.Dynamics.Body cellBody) {
            if (cachedCellBody != cellBody) {
                cachedCellBody = cellBody;
                lookupCellBody = cellBody;
                cachedLevelWall = Level.Loaded?.ExtraWalls.Find(cellBodyMatch) as DestructibleLevelWall;
            }
            return cachedLevelWall;
        }

        private void HitCharactersInBitZone(float deltaTime, Vector2 axis) {
            if (tool == null || GameMain.NetworkMember is { IsClient: true }) { return; }
            foreach (Character c in Character.CharacterList) {
                if (!c.Enabled || c.Removed || c.IsDead) { continue; }
                if (!IsInZone(c.WorldPosition, DrillOrigin(), axis, CharacterZoneLength, CharacterZoneWidth)) { continue; }

                Limb limb = c.AnimController.MainLimb;
                if (limb == null || limb.Removed || limb.IgnoreCollisions) { continue; }

                c.LastDamageSource = item;
                tool.ApplyStatusEffects(ActionType.OnUse, deltaTime, character: c, targetLimb: limb, useTarget: c);
                c.AddDamage(c.WorldPosition, AfflictionPrefab.InternalDamage.Instantiate(CharacterDamage * deltaTime).ToEnumerable(), stun: 0f, playSound: false);
            }
        }

        public override void ReceiveSignal(Signal signal, Connection connection) {
            bool newState = State;
            switch (connection.Name) {
                case "set_state":
                case "set_active":
                    newState = signal.value != "0";
                    break;
                case "toggle":
                    if (signal.value != "0") { newState = !State; }
                    break;
                default:
                    return;
            }
            if (newState != State) {
                State = newState;
                OnStateChanged();
            }
        }
    }
}
