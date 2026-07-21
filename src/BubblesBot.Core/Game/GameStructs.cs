using System.Runtime.InteropServices;

namespace BubblesBot.Core.Game;

/// <summary>
/// Blittable structs mirroring PoE memory layout for direct <c>ReadStruct&lt;T&gt;</c> reads.
/// Each documented with its offset table from community-offsets.md.
/// All use <c>LayoutKind.Explicit</c> with <c>Size</c> set to the struct's known byte count.
/// </summary>

// â”€â”€ Native container types â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>std::vector layout â€” 3 pointers (first/last/end), 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StdVector
{
    public nint First;
    public nint Last;
    public nint End;

    public readonly long Count => ((long)Last - (long)First) / ElementSize;
    public readonly long ByteCount => (long)Last - (long)First;
    /// <summary>Byte size of each element. Default 1 for raw byte count. Set to 8 for pointer arrays.</summary>
    public int ElementSize { get; init; }
}

/// <summary>Native pointer array â€” 3 pointers, elements are 8 bytes each (x64 pointers).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativePtrArray
{
    public nint First;
    public nint Last;
    public nint End;

    public readonly long Count => ((long)Last - (long)First) / 8;
}

/// <summary>PoE's UTF-16 string with small-string optimization (inline when Length &lt;= 7).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeUtf16Text
{
    public nint Buffer;
    public long Reserved8Bytes;
    public long Length;
    public long LengthWithNullTerminator;
}

/// <summary>PoE's UTF-8 string â€” used for component names and metadata paths.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeUtf8Text
{
    public nint Buffer;
    public long Reserved8Bytes;
    public int Length;
    public int LengthWithNullTerminator;
}

// â”€â”€ Math types â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

[StructLayout(LayoutKind.Sequential)]
public struct Vector2
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector3
{
    public float X;
    public float Y;
    public float Z;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector2i
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct ColorBGRA
{
    public byte B;
    public byte G;
    public byte R;
    public byte A;
}

// â”€â”€ Vital / Life â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>
/// Health / Mana / EnergyShield pool. Size = 0x34 (52 bytes).
///   0x10 int32  ReservedFlat
///   0x14 int32  ReservedFraction
///   0x28 float  Regen
///   0x2C int32  Max
///   0x30 int32  Current
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x34)]
public struct VitalStruct
{
    [FieldOffset(0x10)] public int ReservedFlat;
    [FieldOffset(0x14)] public int ReservedFraction;
    [FieldOffset(0x28)] public float Regen;
    [FieldOffset(0x2C)] public int Max;
    [FieldOffset(0x30)] public int Current;

    // Upper sanity bound on a life pool. The old 10,000,000 cap was too low for endgame PoE1:
    // Simulacrum bosses (Kosis, ~30.7M HP live-verified 2026-07-16) and other juiced/pinnacle
    // monsters legitimately exceed 10M, so a real boss read was rejected as garbage → the bot saw
    // it as HP 0/0 → "dead" → never targeted (blocked every Simulacrum boss kill). 1,000,000,000
    // keeps ample headroom for any real monster while still rejecting pointer-derived garbage
    // (the Current/ReservedFlat cross-checks below reject the rest).
    private const int MaxPlausibleVital = 1_000_000_000;

    public readonly bool LooksValid()
    {
        if (Max <= 0 || Max > MaxPlausibleVital) return false;
        if (Current < -Max || Current > Max + 1) return false;
        return ReservedFlat >= 0 && ReservedFlat <= Max;
    }
}

// â”€â”€ Actor â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Deployed object entry (totems, mines, traps, brands). Size = 0xC.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0xC)]
public struct ActorDeployedObject
{
    [FieldOffset(0x0)] public uint EntityId;
    [FieldOffset(0x4)] public ushort SkillId;
    [FieldOffset(0x8)] public ushort ObjectType;
}

/// <summary>Per-skill cooldown state. Size = 0x3E.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x3E)]
public struct ActorSkillCooldown
{
    [FieldOffset(0x8)]  public int SkillSubId;
    [FieldOffset(0x10)] public StdVector Cooldowns;
    [FieldOffset(0x30)] public int MaxUses;
    [FieldOffset(0x3C)] public ushort SkillId;
}

/// <summary>Single actor skill slot. Size = 0x18+.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public struct ActorSkill
{
    [FieldOffset(0x8)]  public byte SkillUseStage;
    [FieldOffset(0xC)]  public byte CastType;
    // +0x10 SubActorSkillOffsets SubData â€” omitted for now, AutoExile doesn't read it
}

// â”€â”€ Animation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Animation controller attached to Actor. Size = 0x200.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x200)]
public struct AnimationController
{
    [FieldOffset(0x18)]  public NativePtrArray ActiveAnimationsArrayPtr;
    [FieldOffset(0x180)] public nint ActorAnimationArrayPtr;
    [FieldOffset(0x190)] public int AnimationInActorId;
    [FieldOffset(0x1A4)] public float AnimationProgress;
    [FieldOffset(0x1A8)] public int CurrentAnimationStage;
    [FieldOffset(0x1AC)] public float NextAnimationPoint;
    [FieldOffset(0x1B0)] public float AnimationSpeedMultiplier1;
    [FieldOffset(0x1B8)] public float MaxAnimationProgressOffset;
    [FieldOffset(0x1BC)] public float MaxAnimationProgress;
    [FieldOffset(0x1F8)] public float AnimationSpeedMultiplier2;
}

// â”€â”€ Buffs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>A single buff instance. Size = 0x4C.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x4C)]
public struct Buff
{
    [FieldOffset(0x8)]  public nint BuffDatPtr;
    [FieldOffset(0x18)] public float MaxTime;
    [FieldOffset(0x1C)] public float Timer;
    [FieldOffset(0x28)] public uint SourceEntityId;
    [FieldOffset(0x40)] public ushort Charges;
    [FieldOffset(0x42)] public ushort FlaskSlot;
    [FieldOffset(0x48)] public ushort SourceSkillId;
    [FieldOffset(0x4A)] public ushort SourceSkillId2;

    public readonly float RemainingRatio => MaxTime > 0 ? Timer / MaxTime : 0;
}

// â”€â”€ Action â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Current action being performed by an Actor. Size = 0x138.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x138)]
public struct ActionWrapper
{
    [FieldOffset(0xF8)]  public long Skill;       // Int64 â€” pointer to skill
    [FieldOffset(0x128)] public long Target;       // Int64 â€” pointer to target entity
    [FieldOffset(0x130)] public Vector2i Destination;
}

// â”€â”€ Camera â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Camera inner struct â€” view/projection matrices. Size = 0x278.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x278)]
public struct CameraInner
{
    // Matrix4x4 at +0x100 â€” 16 floats = 64 bytes
    [FieldOffset(0x174)] public Vector3 Position;
    [FieldOffset(0x214)] public float ZFar;
    [FieldOffset(0x270)] public int Width;
    [FieldOffset(0x274)] public int Height;
}

// â”€â”€ UI Element â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Base UI element. Large struct (~0x4B0+ bytes). Only key fields mapped.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x4C0)]
public struct Element
{
    [FieldOffset(0xB0)]  public nint SelfPointer;
    [FieldOffset(0xB8)]  public NativePtrArray Childs;
    [FieldOffset(0x130)] public Vector2 ScrollOffset;
    [FieldOffset(0x148)] public Vector2 Position;
    [FieldOffset(0x160)] public nint Root;
    [FieldOffset(0x188)] public byte LabelTextSize;
    [FieldOffset(0x18C)] public float Scale;
    [FieldOffset(0x1C8)] public ushort Type;
    [FieldOffset(0x1D0)] public nint Parent;
    [FieldOffset(0x1D8)] public uint Flags;
    [FieldOffset(0x1E8)] public nint Tooltip;
    [FieldOffset(0x258)] public Vector2 Size;
    [FieldOffset(0x278)] public ColorBGRA LabelBackgroundColor;
    [FieldOffset(0x27C)] public ColorBGRA LabelTextColor;
    [FieldOffset(0x280)] public ColorBGRA LabelBorderColor;
    [FieldOffset(0x294)] public byte ShinyHighlightState;
    [FieldOffset(0x328)] public nint TextureNamePtr;
    [FieldOffset(0x380)] public NativeUtf16Text Text;
    [FieldOffset(0x4A8)] public NativeUtf16Text TextNoTags;
}

// â”€â”€ Item components â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Base component on items â€” name, corruption, influence. Size = 0xD0.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0xD0)]
public struct BaseComponent
{
    [FieldOffset(0x10)] public nint ItemInfo;
    [FieldOffset(0x60)] public NativeUtf16Text PublicPrice;
    [FieldOffset(0xC5)] public byte CurrencyItemLevel;
    [FieldOffset(0xC6)] public byte Influence;
    [FieldOffset(0xC7)] public byte Corrupted;
    [FieldOffset(0xC8)] public int UnspentAbsorbedCorruption;
    [FieldOffset(0xCC)] public int ScourgedTier;
}

/// <summary>Current-build socket colors plus link-group-size vector. Size = 0x40.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x40)]
public struct SocketsComponent
{
    [FieldOffset(0x10)] public int Socket1;
    [FieldOffset(0x14)] public int Socket2;
    [FieldOffset(0x18)] public int Socket3;
    [FieldOffset(0x1C)] public int Socket4;
    [FieldOffset(0x20)] public int Socket5;
    [FieldOffset(0x24)] public int Socket6;
    [FieldOffset(0x28)] public StdVector LinkSizes;
}

/// <summary>Area transition (door, portal, zone entrance). Size = 0x150.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x150)]
public struct AreaTransitionComponent
{
    [FieldOffset(0xA8)]  public ushort AreaId;
    [FieldOffset(0xB2)]  public byte TransitionType;
    [FieldOffset(0x148)] public nint WorldAreaInfoPtr;
}

/// <summary>Chest / strongbox. Size = 0x1B0.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x1B0)]
public struct ChestComponent
{
    [FieldOffset(0x160)] public nint StrongboxData;
    [FieldOffset(0x168)] public byte IsOpened;
    [FieldOffset(0x169)] public byte IsLocked;
    [FieldOffset(0x16C)] public byte Quality;
    [FieldOffset(0x1A8)] public byte IsStrongbox;
}

// â”€â”€ Entity list â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>EntityList wrapper struct. Size = 0x30.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x30)]
public struct EntityListStruct
{
    [FieldOffset(0x0)]  public nint FirstAddr;
    [FieldOffset(0x10)] public nint SecondAddr;
    [FieldOffset(0x19)] public byte IsEmpty;
    [FieldOffset(0x28)] public nint Entity;
}

// â”€â”€ Currency exchange â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>CurrencyExchangePanel offsets â€” Size approx 0x570.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x570)]
public struct CurrencyExchangePanel
{
    [FieldOffset(0x3C8)] public nint WantedItemCountInputPtr;
    [FieldOffset(0x3D0)] public nint WantedItemTypePtr;
    [FieldOffset(0x430)] public StdVector Stock1;
    [FieldOffset(0x448)] public StdVector Stock2;
    [FieldOffset(0x470)] public short MarketRateGet;
    [FieldOffset(0x472)] public short MarketRateGive;
    [FieldOffset(0x560)] public StdVector OrderList;
}

// â”€â”€ Components from ExileApi source â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Targetable component â€” whether entity can be/is targeted. Size approx 0x48.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x48)]
public struct TargetableComponent
{
    [FieldOffset(0x30)] public byte IsTargetable;
    [FieldOffset(0x32)] public byte IsTargeted;
}

/// <summary>Stats component â€” stat array. Size approx 0xA0.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0xA0)]
public struct StatsComponent
{
    [FieldOffset(0x8)]  public long Owner;
    [FieldOffset(0x88)] public NativePtrArray Stats;
}

/// <summary>Mods component â€” item mods, rarity, item level. Large struct (~0x440).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x440)]
public struct ModsComponent
{
    [FieldOffset(0x30)]  public long UniqueName;
    [FieldOffset(0xB0)]  public byte Identified;
    [FieldOffset(0xB4)]  public int ItemRarity;
    [FieldOffset(0x180)] public NativePtrArray ItemStats;       // flattened (int32 statId, int32 value) records — see KnownOffsets.ItemStatRecord

    [FieldOffset(0x170)] public NativePtrArray GetImplicitStats;
    [FieldOffset(0x1A0)] public NativePtrArray GetStats;
    [FieldOffset(0x1B8)] public NativePtrArray GetCraftedStats;
    [FieldOffset(0x1D0)] public NativePtrArray GetFracturedStats;
    [FieldOffset(0x370)] public byte IsUsable;
    [FieldOffset(0x371)] public byte IsMirrored;
    [FieldOffset(0x248)] public int ItemLevel;
    [FieldOffset(0x24C)] public int RequiredLevel;
}

/// <summary>Stack component â€” stackable item count. Size approx 0x20.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x20)]
public struct StackComponent
{
    [FieldOffset(0x10)] public long StackInternalPtr;
    [FieldOffset(0x18)] public int CurrentCount;
}

/// <summary>Quality component â€” item quality %. Size approx 0x1C.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x1C)]
public struct QualityComponent
{
    [FieldOffset(0x18)] public int CurrentQuality;
}

/// <summary>SkillGem component â€” gem level/exp. Size approx 0x38.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x38)]
public struct SkillGemComponent
{
    [FieldOffset(0x20)] public long SkillGemInternalPtr;
    [FieldOffset(0x28)] public uint TotalExpGained;
    [FieldOffset(0x2C)] public uint Level;
    [FieldOffset(0x30)] public uint ExperiencePrevLevel;
    [FieldOffset(0x34)] public uint ExperienceMaxLevel;
}

/// <summary>WorldItem component â€” link to item entity. Size approx 0x48.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x48)]
public struct WorldItemComponent
{
    [FieldOffset(0x18)] public long ItemPtr;
    [FieldOffset(0x20)] public int LootAllocationId;
    [FieldOffset(0x24)] public uint LootAllocationTime;
}

/// <summary>RenderItem component â€” item render resource path. Size approx 0x28.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x28)]
public struct RenderItemComponent
{
    [FieldOffset(0x10)] public NativeUtf16Text ResourcePath;
}

