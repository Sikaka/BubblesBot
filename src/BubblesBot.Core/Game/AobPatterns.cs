namespace BubblesBot.Core.Game;

/// <summary>
/// Stores byte patterns for AOB-scanning PoE.exe's .text section to locate global pointer slots.
/// Each pattern targets a REX.W MOV reg,[RIP+rel32] instruction that loads a root game object.
///
/// How to populate after a PoE patch:
///   1. Run: BubblesBot.Research --discover-aob
///      (requires PoE running + POEMCP active to supply the ground-truth addresses)
///   2. The tool prints pattern literals like:
///         new byte?[] { 0x48, 0x8B, 0x0D, null, null, null, null, 0xE8, ... }
///         // DispOffset=3, InstrLen=7
///   3. Paste into the appropriate array below.
///
/// How patterns are used at runtime:
///   AobScanner.ScanForResolvedAddresses finds .text matches, resolves RIP+disp â†’ global slot address.
///   ReadPointer(slotAddress) = the live heap address of the game object.
///   If no patterns are stored (arrays empty), startup falls back to value-scan (--hp N argument).
/// </summary>
public static class AobPatterns
{
    public sealed record Pattern(
        byte?[] Bytes,
        int DispOffset,
        int InstrLen,
        string Description);

    /// <summary>
    /// Patterns that RIP-reference the global pointer slot for IngameState.
    /// The resolved slot address, when dereferenced, gives the IngameState heap object.
    ///
    /// Populate by running: BubblesBot.Research --discover-aob
    /// </summary>
    public static readonly Pattern[] IngameStateRefs =
    [
        new Pattern(
            Bytes: new byte?[] {
                0x8B, 0xC7, 0x48, 0x83, 0xC4, 0x40, 0x5F, 0xC3,
                0x48, 0x8B, 0x05, null, null, null, null,
                0x48, 0x89, 0x07, 0x48
            },
            // Pattern includes 8 prefix bytes before the MOV — adjust offsets accordingly.
            // Instruction-relative DispOffset=3, InstrLen=7 → pattern-relative 8+3=11, 8+7=15.
            DispOffset:  11,
            InstrLen:    15,
            Description: "IngameState global slot — PoE build 2026-05-05 (validated by --discover-aob)"),
    ];

    /// <summary>
    /// Patterns that RIP-reference a global slot holding the <b>game-root container</b>.
    /// Unlike IngameState, TheGame has no direct global: PoE keeps a container object whose
    /// field at <c>KnownOffsets.TheGame.ContainerTheGamePtr</c> (+0xA00) points to TheGame.
    /// Resolution: pattern → slot → container → read +0xA00 → shape-check via
    /// <c>TheGameResolver.LooksLikeTheGame</c>. Note: the heap holds decoy/copy TheGame
    /// structures with obfuscated state slots — only the container-reachable one is live.
    ///
    /// Three independent slots hold the same container; one pattern per slot so a single
    /// changed code path doesn't strand the gate. All neighbor displacements are wildcarded
    /// (they shift every build even when the code doesn't).
    ///
    /// Re-populate per patch by running: BubblesBot.Research --discover-thegame
    /// (oracle-free — derives TheGame from the resolved IngameState, no POEMCP required).
    /// </summary>
    public static readonly Pattern[] TheGameRefs =
    [
        // mov [rip+X], rax / mov [rip+Y], edx / mov [rip+SLOT], rdx / call ... / lea rcx,[rip+Z] / call
        // Store of the container pointer in the init routine (.text RVA 0x1DD22F, build 2026-07-13).
        new Pattern(
            Bytes: new byte?[] {
                0x48, 0x89, 0x05, null, null, null, null,
                0x89, 0x15, null, null, null, null,
                0x48, 0x89, 0x15, null, null, null, null,
                0xE8, null, null, null, null,
                0x48, 0x8D, 0x0D, null, null, null, null,
                0xE8
            },
            DispOffset:  16,
            InstrLen:    20,
            Description: "Game-root container slot A (mov [rip],rdx) — PoE build 2026-07-13 (--discover-thegame)"),

        // mov [rip+X], rbp / xorps xmm0,xmm0 / movdqa [rip+SLOT], xmm0 / xorps xmm1,xmm1 / movdqa [rip+Y], xmm1
        // SSE zero-init of the slot (.text RVA 0x840A72, build 2026-07-13).
        new Pattern(
            Bytes: new byte?[] {
                0x48, 0x89, 0x2D, null, null, null, null,
                0x0F, 0x57, 0xC0,
                0x66, 0x0F, 0x7F, 0x05, null, null, null, null,
                0x0F, 0x57, 0xC9,
                0x66, 0x0F, 0x7F, 0x0D, null, null, null, null,
                0x66, 0x0F, 0x7F, 0x05
            },
            DispOffset:  14,
            InstrLen:    18,
            Description: "Game-root container slot B (movdqa [rip],xmm0) — PoE build 2026-07-13 (--discover-thegame)"),

        // mov [rip+X], ebp / mov [rip+Y], rbp / mov [rip+SLOT], rbp / lea rcx,[rip+Z] / call / nop
        // Null-init store run (.text RVA 0x840AB9, build 2026-07-13).
        new Pattern(
            Bytes: new byte?[] {
                0x89, 0x2D, null, null, null, null,
                0x48, 0x89, 0x2D, null, null, null, null,
                0x48, 0x89, 0x2D, null, null, null, null,
                0x48, 0x8D, 0x0D, null, null, null, null,
                0xE8, null, null, null, null,
                0x90
            },
            DispOffset:  16,
            InstrLen:    20,
            Description: "Game-root container slot C (mov [rip],rbp) — PoE build 2026-07-13 (--discover-thegame)"),
    ];

    // Future root objects can be added here in the same form:
    //
    // public static readonly Pattern[] FileRootRefs = [];

    /// <summary>
    /// Pattern that locates a <b>field-offset</b> within a struct, by matching an instruction
    /// whose displacement IS the field offset. Example: <c>mov rax, [rdx+0x218]</c> bytes
    /// <c>48 8B 82 18 02 00 00</c> — the four <c>18 02 00 00</c> bytes encode the offset.
    ///
    /// <para>To extract the offset after a patch:</para>
    /// <list type="number">
    ///   <item>AOB-scan for the surrounding pattern (the wildcards mark the disp bytes).</item>
    ///   <item>Read <see cref="DispWidth"/> bytes at <see cref="DispOffsetInMatch"/> within the
    ///         matched bytes — that's the new field offset (sign-extended for disp8).</item>
    /// </list>
    ///
    /// <para><b>Stability requires a unique signature.</b> The bare instruction shape
    /// (<c>48 8B 82 ?? ?? ?? ??</c>) repeats throughout PoE.exe — without surrounding context
    /// the pattern matches many unrelated accesses. Each catalog entry needs enough prefix or
    /// suffix bytes that exactly one match exists in <c>.text</c>. Generating those signatures
    /// is currently manual disassembly work; <see cref="AobScanner.FindFieldAccessHits"/>
    /// helps by listing every candidate hit with surrounding context.</para>
    /// </summary>
    public sealed record FieldOffsetPattern(
        string FieldName,
        byte?[] Bytes,
        int     DispOffsetInMatch,   // byte position WITHIN Bytes where the displacement starts
        int     DispWidth,           // 1 (disp8 sign-extended) or 4 (disp32)
        string  Description);

    /// <summary>
    /// Catalog of field-offset patterns. Empty for now — populating requires manual
    /// disassembly to find unique-enough surrounding bytes per offset. Once an entry exists,
    /// <see cref="AobScanner.ExtractFieldOffset"/> recovers the new value automatically per
    /// patch.
    /// </summary>
    public static readonly FieldOffsetPattern[] FieldPatterns = Array.Empty<FieldOffsetPattern>();
}
