using System.Buffers.Binary;

namespace PhiveSharp;

/// <summary>
/// One entry of a material palette: what a mesh primitive's shape tag stands for. A shape keeps its
/// palette in sections 1 and 2; a static compound keeps every body's in sections 5 and 6, and a body
/// names its run of them.
/// </summary>
/// <param name="Material">The material, sixteen bytes into the palette entry with the next two.</param>
/// <param name="SubMaterial">The sub material.</param>
/// <param name="Flags">Collision flags, 0x8000 on most entries.</param>
/// <param name="Filter">
/// The collision filter, from the separate eight byte table: the layers it hits in the low half and the
/// sub layers in the high half.
/// </param>
public readonly record struct PhiveMaterial(int Material, int SubMaterial, ulong Flags, ulong Filter)
{
    internal const int Size = 16;
    internal const int FilterSize = 8;

    /// <summary>The material, by <c>MaterialCollection</c> in <c>Phive/Config/PhiveConfig.byml</c>.</summary>
    public string? MaterialName => Material >= 0 && Material < MaterialNames.Count ? MaterialNames[Material] : null;

    /// <summary>The sub material, by <c>SubMaterialCollection</c>.</summary>
    public string? SubMaterialName => SubMaterial >= 0 && SubMaterial < SubMaterialNames.Count ? SubMaterialNames[SubMaterial] : null;

    /// <summary>The flags that are set, by <c>UserShapeTagCollection</c>, which names them bit by bit.</summary>
    public IEnumerable<string> FlagNameList => Bits(Flags, FlagNames);

    /// <summary>The entity layers this hits, a bit for each of <see cref="LayerNames"/>.</summary>
    public uint LayerHitMask => (uint)Filter;

    /// <summary>The entity sub layers this hits, a bit for each of <see cref="SubLayerNames"/>.</summary>
    public uint SubLayerHitMask => (uint)(Filter >> 32);

    public IEnumerable<string> HitLayers => Bits(LayerHitMask, LayerNames);

    public IEnumerable<string> HitSubLayers => Bits(SubLayerHitMask, SubLayerNames);

    private static IEnumerable<string> Bits(ulong mask, IReadOnlyList<string> names)
    {
        for (int i = 0; i < names.Count; i++)
            if ((mask & (1UL << i)) != 0) yield return names[i];
    }

    public override string ToString() =>
        $"{MaterialName ?? Material.ToString()}/{SubMaterialName ?? SubMaterial.ToString()}" +
        (Flags != 0 ? $" [{string.Join(", ", FlagNameList)}]" : "");

    public static IReadOnlyList<string> MaterialNames { get; } =
    [
        "Undefined", "Soil", "Grass", "Sand", "HeavySand", "Snow", "HeavySnow", "Stone", "StoneSlip", "StoneNoSlip",
        "SlipBoard", "Cart", "Metal", "MetalSlip", "MetalNoSlip", "WireNet", "Wood", "Ice", "Cloth", "Glass",
        "Bone", "Rope", "Character", "Ragdoll", "Surfing", "GuardianFoot", "LaunchPad", "Conveyer", "Rail",
        "Grudge", "Meat", "Vegetable", "Bomb", "MagicBall", "Barrier", "AirWall", "GrudgeSlow", "Tar", "Water",
        "HotWater", "IceWater", "Lava", "Bog", "ContaminatedWater", "DungeonCeil", "Gas", "InvalidateRestartPos",
        "HorseSpeedLimit", "ForbidDynamicCuttingAreaForHugeCharacter", "ForbidHorseReturnToSafePosByClimate",
        "Dragon", "ReferenceSurfing", "WaterSlip", "HorseDeleteImmediatelyOnDeath"
    ];

    public static IReadOnlyList<string> SubMaterialNames { get; } =
    [
        "Undefined", "Soft", "Hard", "Light", "Heavy", "Marble", "DgnLight", "DgnHeavy", "Shallow", "Deep", "Moss",
        "Long", "WithMud", "Leaf", "Straw", "Thatch", "Thin", "Thick", "Ice", "Hot", "Poison", "StoneBottom",
        "Sherbet", "Carpet", "Leather", "Pile", "Solid", "HolyWall", "PriestWall", "RemainsWind", "DungeonAirWall",
        "Spike", "SpikeWeak", "ZonauLight", "ZonauHeavy", "Black", "Yellow", "Floating", "RingRope",
        "HorseReturnToSafePos", "karesansui", "Spore", "Whirlpool", "Quicksand", "Terrain", "TerrainShallow",
        "Purple", "WithStone", "Bushy", "StoneZonau"
    ];

    public static IReadOnlyList<string> LayerNames { get; } =
    [
        "Object", "SmallObject", "GroundObject", "Player", "Friendly", "Character", "HugeCharacter", "Water",
        "GroundHighRes", "GroundHighResSmooth", "GroundHighResRough", "GroundStaticTree", "HitOnlyGround",
        "CharacterSight", "CameraSight", "CameraBody", "NoHit", "CustomReceiver", "Fluid", "UltraHandBlocker",
        "Smoke", "Gas", "Tar", "LowTree", "IK", "RiddenRaumiGolem"
    ];

    public static IReadOnlyList<string> SubLayerNames { get; } =
    [
        "Unspecified", "Animal", "Enemy", "NPC", "ClimbingPlayer", "AttackHitEnemy", "Arrow", "Blast",
        "EnemyVehicle", "IK", "MovingTrolley", "Rope", "AirWall", "Light", "Fire", "FloatingWater", "Ice", "Cave",
        "DetectMarking", "Waterfall", "CeilingClipper", "Beam", "WaterObj", "ForceField", "Sage", "SoulSage",
        "Destructible", "AtTgOcclusionCheck", "DiveAttacking", "CaveMaster", "TrackingKinematicBody",
        "BondConnection"
    ];

    public static IReadOnlyList<string> SensorLayerNames { get; } =
    [
        "Object", "SmallObject", "Player", "Enemy", "EnemyZonau", "NPC", "Friendly", "Animal", "TgtHitAll",
        "PlayerAttack", "EnemyAttack", "EnemyZonauAttack", "NPCAttack", "FriendlyAttack", "AttackAll",
        "ChemicalObject", "ChemicalElement", "InDoor", "StaticGeneralArea", "QueryOnly", "Tree", "Camera", "NoHit",
        "CustomReceiver", "PerceptionSensor", "PerceptionEmitter", "Miasma", "Fluid", "ForbidAutoPlacementArea",
        "SpecialChemical", "GeneralArea", "Tar"
    ];

    public static IReadOnlyList<string> SensorSubLayerNames { get; } =
    [
        "Unspecified", "PerceptionPlayer", "PerceptionCapturable", "PerceptionEnemy", "PerceptionZonauEnemy",
        "PerceptionEquipment", "PerceptionDestructible", "PerceptionHorse", "PerceptionInterestEmitActor",
        "PerceptionLiftable", "PerceptionNPC", "PerceptionNPCNoCombat", "PerceptionObject", "PerceptionPrey",
        "PerceptionPredator", "PerceptionSage", "PerceptionShootable", "PerceptionCaveMaster", "PerceptionItemOreG",
        "Area", "LocalTemperature", "Rope", "SpecialChemical", "Dust", "ForEnergyGauge", "LocalTemperatureWarm",
        "DestructibleActor", "EnemyLookoutArea", "SidonWaterArea", "LowGravityArea", "SageLightOrbArea",
        "LocationArea"
    ];

    public static IReadOnlyList<string> FlagNames { get; } =
    [
        "NoClimb", "LadderUp", "Ladder", "Slip", "LadderSide", "NoSlipRainOnClimb", "NoDashUp", "Abyss",
        "NarrowPlace", "TopBroadLeafTree", "TopConiferousTree", "FlowFall", "TrolleyRotationFloor",
        "InvalidateRestartPosForHorse", "NoPreventFall", "NoStick", "IndoorCamera", "IndoorWithWall",
        "IndoorWithRoof", "IndoorStatIgnore", "HyurleCastle", "EnableWallJump", "Wet", "EdgeOfTheWorld",
        "DisableRespawn", "DisableSurfaceVelocity", "DisableImpulse", "StopUntilStickNeutral",
        "DontIgnoreInertiaForce", "HeadShot", "DontGenerateNavMesh", "Unridable", "SightDown", "Miasma",
        "DisableContactExceptAlready", "MiasmaLv1", "ForbiddenGeneratingEnemy", "ForbiddenGeneratingAnimal",
        "ForbiddenGeneratingInsect", "ForbiddenGeneratingFish", "ForbiddenGeneratingBird",
        "ForbiddenGeneratingMaterial", "NonEnemySearch", "IgnoredByNavMesh", "CeilingClipperForceAccept",
        "StopCeilingClipper", "Spike_Y_Minus", "Dragon", "ForbiddenGeneratingSkyFallenBox", "BakeInstanceID",
        "NoEntrySage", "MiasmaCancel", "Sunlight", "Confusion", "NoWallSlip", "Fruit", "ShrineEntrance",
        "NavMeshNPCOnly", "NavMeshGiantNoEntry", "MiasmaLv3", "CameraAtBlock", "MiasmaEntity", "IgnoreAttach",
        "IgnoreBondConnection"
    ];

    internal static List<PhiveMaterial> Read(ReadOnlySpan<byte> entries, ReadOnlySpan<byte> filters, int first, int count)
    {
        if (first < 0 || count < 0 || (first + count) * Size > entries.Length ||
            !filters.IsEmpty && (first + count) * FilterSize > filters.Length)
            throw new InvalidDataException($"Materials {first}..{first + count} run past the palette.");

        List<PhiveMaterial> result = new(count);
        for (int i = first; i < first + count; i++)
            result.Add(new PhiveMaterial(
                BinaryPrimitives.ReadInt32LittleEndian(entries[(i * Size)..]),
                BinaryPrimitives.ReadInt32LittleEndian(entries[(i * Size + 4)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entries[(i * Size + 8)..]),
                filters.IsEmpty ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(filters[(i * FilterSize)..])));
        return result;
    }

    /// <summary>Both tables, each rounded up to sixteen bytes the way the shipped files are.</summary>
    internal static (byte[] Entries, byte[] Filters) Write(IReadOnlyList<PhiveMaterial> materials)
    {
        byte[] entries = new byte[materials.Count * Size];
        byte[] filters = new byte[(materials.Count * FilterSize + 15) & ~15];
        for (int i = 0; i < materials.Count; i++)
        {
            PhiveMaterial m = materials[i];
            BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(i * Size), m.Material);
            BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(i * Size + 4), m.SubMaterial);
            BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(i * Size + 8), m.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(filters.AsSpan(i * FilterSize), m.Filter);
        }
        return (entries, filters);
    }
}
