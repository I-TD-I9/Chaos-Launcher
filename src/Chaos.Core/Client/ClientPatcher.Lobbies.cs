namespace Chaos.Client;

public static partial class ClientPatcher
{
    // SELECT LOBBY lists only the lobbies a character may enter. sub_662e30 walks the gate's
    // directory rank by rank, and for each lobby jumps by its rank through the table at 0x662fe8
    // to that rank's rule, which goes on either to the add at 0x662faf or to the skip at 0x662fb9.
    // A beginner never sees Lv. 2~8 or GUNSLINGER, and a player past Lv. 1 never sees Beginners.
    //
    // The copy lists those lobbies too, faded, and lets nobody into them. The rules for rank 1
    // (Beginners) and for ranks 5 and 7 (Lv. 2~8 and GUNSLINGER) go to routines in .chaos that ask
    // what the client asks, but end at the add either way, marking a lobby the character may not
    // enter in byte +0x31 of its directory record, which the gate never sends and nothing in the
    // client reads. A lobby of rank 5 or 7 is also marked whenever its load is 1000 or more: the
    // server sends that for GUNSLINGER while no event is on, and keeps every open lobby's load at
    // 999 or below.
    //
    // A plate has an enabled flag at +0x2c that the client sets once, in its constructor
    // (0x661eb4), and its hit tests give up on when it is clear (0x662366, 0x662443). The plate's
    // fill sets it from the record's mark, so a faded plate never lights up, presses or opens, and
    // its draw passes 0x60 rather than the scene's 0xff (0x6643f3) to every image and text line of
    // a disabled plate. The record's constructor writes +0x31 as 0, so no other plate is faded.
    //
    // A closed GUNSLINGER plate would read " 1000", so its number is formatted with an empty
    // string instead of "% 5d"; and the region screen lights its GUNSLINGER effect when the
    // directory has any rank-7 lobby, so it now skips one that is closed.
    internal const uint LockedLobbyVa = 0xB98800;
    internal const uint ClosedNumberVa = 0xB98890;
    internal const uint EmptyStringVa = 0xB988AF;
    internal const uint RegionEffectVa = 0xB988B0;

    // The routines, 135 bytes from LockedLobbyVa. The entry points are rank1, rank7, fill and draw.
    internal static ReadOnlySpan<byte> LockedLobbyStub =>
    [
        // rank1: from the table's slot for rank 1, with ecx the record, esi the character's level,
        // edi the character and [esp+0x10] its duels, as at 0x662f23
        0xC6, 0x41, 0x31, 0x00,                // mov byte ptr [ecx + 0x31], 0, open
        0x83, 0x7C, 0x24, 0x10, 0x23,          // cmp dword ptr [esp + 0x10], 0x23, 35 duels
        0x7C, 0x14,                            // jl rank1_add
        0x66, 0x81, 0xBF, 0xCA, 0x00, 0x00, 0x00, 0x01, 0xF0, // cmp word ptr [edi + 0xca], 0xf001, GAME MASTER
        0x74, 0x09,                            // je rank1_add
        0x83, 0xFE, 0x01,                      // cmp esi, 1
        0x7E, 0x04,                            // jle rank1_add
        0xC6, 0x41, 0x31, 0x01,                // mov byte ptr [ecx + 0x31], 1, closed to this character
        // rank1_add:
        0xE9, 0x8B, 0xA7, 0xAC, 0xFF,          // jmp 0x662faf, the add

        // rank7: from the table's slots for ranks 5 and 7, as at 0x662f7e
        0xC6, 0x41, 0x31, 0x00,                // mov byte ptr [ecx + 0x31], 0
        0x0F, 0xBF, 0x41, 0x2C,                // movsx eax, word ptr [ecx + 0x2c], the load
        0x3D, 0xE8, 0x03, 0x00, 0x00,          // cmp eax, 1000
        0x7D, 0x17,                            // jge rank7_lock, no event on
        0x66, 0x81, 0xBF, 0xCA, 0x00, 0x00, 0x00, 0x01, 0xF0, // cmp word ptr [edi + 0xca], 0xf001
        0x74, 0x10,                            // je rank7_add
        0x83, 0xFE, 0x02,                      // cmp esi, 2
        0x7C, 0x07,                            // jl rank7_lock
        0x83, 0x7C, 0x24, 0x10, 0x23,          // cmp dword ptr [esp + 0x10], 0x23
        0x7D, 0x04,                            // jge rank7_add
        // rank7_lock:
        0xC6, 0x41, 0x31, 0x01,                // mov byte ptr [ecx + 0x31], 1
        // rank7_add:
        0xE9, 0x5C, 0xA7, 0xAC, 0xFF,          // jmp 0x662faf

        // fill: called from 0x662d7f in sub_662af0, with esi the plate and ebp the record
        0xC6, 0x46, 0x10, 0x01,                // mov byte ptr [esi + 0x10], 1, as the client does
        0x8A, 0x45, 0x31,                      // mov al, byte ptr [ebp + 0x31]
        0x34, 0x01,                            // xor al, 1
        0x88, 0x46, 0x2C,                      // mov byte ptr [esi + 0x2c], al, enabled unless closed
        0x84, 0xC0,                            // test al, al
        0x75, 0x08,                            // jne fill_done
        0xC6, 0x46, 0x11, 0x00,                // mov byte ptr [esi + 0x11], 0, not under the mouse
        0xC6, 0x46, 0x12, 0x00,                // mov byte ptr [esi + 0x12], 0, not pressed
        // fill_done:
        0x8A, 0x45, 0x2F,                      // mov al, byte ptr [ebp + 0x2f], as the client does
        0xC3,                                  // ret, to 0x662d84 and its two nops

        // draw: jumped to from 0x66252b in the plate's draw, with esi the plate
        0x8B, 0x6C, 0x24, 0x2C,                // mov ebp, dword ptr [esp + 0x2c], the alpha
        0x8B, 0x7C, 0x24, 0x28,                // mov edi, dword ptr [esp + 0x28]
        0x80, 0x7E, 0x2C, 0x00,                // cmp byte ptr [esi + 0x2c], 0
        0x75, 0x05,                            // jne draw_back
        0xBD, 0x60, 0x00, 0x00, 0x00,          // mov ebp, 0x60, faded
        // draw_back:
        0xE9, 0xAC, 0x9C, 0xAC, 0xFF,          // jmp 0x662533, after the two loads
    ];

    // The closed plate's number, 28 bytes from ClosedNumberVa, jumped to from the push of "% 5d"
    // at 0x662d09 in sub_662af0, where ebp is still the record (0x662d00 reads its load).
    internal static ReadOnlySpan<byte> ClosedNumberStub =>
    [
        0x66, 0x81, 0x7D, 0x2C, 0xE8, 0x03,    // cmp word ptr [ebp + 0x2c], 1000
        0x7D, 0x0A,                            // jge closed
        0x68, 0x20, 0xA9, 0x71, 0x00,          // push 0x71a920, "% 5d", as the client does
        0xE9, 0x6C, 0xA4, 0xAC, 0xFF,          // jmp 0x662d0e
        // closed:
        0x68, 0xAF, 0x88, 0xB9, 0x00,          // push 0xb988af, the zero byte at EmptyStringVa
        0xE9, 0x62, 0xA4, 0xAC, 0xFF,          // jmp 0x662d0e
    ];

    // The region screen's test, 27 bytes from RegionEffectVa, jumped to from 0x69f8b2 in
    // sub_69f520, where ecx is the record (0x69f8a1). It changes only the flags, and 0x69f8bf
    // loads what it needs itself.
    internal static ReadOnlySpan<byte> RegionEffectStub =>
    [
        0x80, 0x79, 0x2F, 0x07,                // cmp byte ptr [ecx + 0x2f], 7, as the client does
        0x0F, 0x85, 0x05, 0x70, 0xB0, 0xFF,    // jne 0x69f8bf, the next record
        0x66, 0x81, 0x79, 0x2C, 0xE8, 0x03,    // cmp word ptr [ecx + 0x2c], 1000
        0x0F, 0x8D, 0xF9, 0x6F, 0xB0, 0xFF,    // jge 0x69f8bf, closed: no effect for it
        0xE9, 0xED, 0x6F, 0xB0, 0xFF,          // jmp 0x69f8b8, the client's test of the area
    ];

    internal static readonly SitePatch[] LobbySites =
    [
        // The table's slot for rank 1, 0x662f23, and for ranks 5 and 7, 0x662f7e; ranks 6 and 9
        // keep the client's rule.
        new("the lobby menu's slot for rank 1", 0x662FE8, [0x23, 0x2F, 0x66, 0x00], [0x00, 0x88, 0xB9, 0x00]),
        new("the lobby menu's slot for rank 5", 0x662FF8, [0x7E, 0x2F, 0x66, 0x00], [0x24, 0x88, 0xB9, 0x00]),
        new("the lobby menu's slot for rank 7", 0x663000, [0x7E, 0x2F, 0x66, 0x00], [0x24, 0x88, 0xB9, 0x00]),
        // mov byte ptr [esi+0x10], 1; mov al, [ebp+0x2f], in the plate's fill
        new("the plate's fill", 0x662D7F, [0xC6, 0x46, 0x10, 0x01, 0x8A, 0x45, 0x2F],
            [0xE8, 0xCF, 0x5A, 0x53, 0x00, 0x90, 0x90]),                                    // call 0xb98853
        // mov ebp, [esp+0x2c]; mov edi, [esp+0x28], at the top of the plate's draw
        new("the plate's draw", 0x66252B, [0x8B, 0x6C, 0x24, 0x2C, 0x8B, 0x7C, 0x24, 0x28],
            [0xE9, 0x3F, 0x63, 0x53, 0x00, 0x90, 0x90, 0x90]),                              // jmp 0xb9886f
        // mov [eax+0x30], cl; mov [eax+0x32], bx, with cl 0xff (0x67addd) and bx 0 (0x67add3), in
        // the directory record's constructor; the same, with +0x31 zero
        new("the record's constructor", 0x67AE15, [0x88, 0x48, 0x30, 0x66, 0x89, 0x58, 0x32],
            [0xC7, 0x40, 0x30, 0xFF, 0x00, 0x00, 0x00]),                                    // mov dword ptr [eax+0x30], 0xff
        // push 0x71a920, "% 5d", the plate's number
        new("the plate's number", 0x662D09, [0x68, 0x20, 0xA9, 0x71, 0x00], [0xE9, 0x82, 0x5B, 0x53, 0x00]), // jmp 0xb98890
        // cmp byte ptr [ecx+0x2f], 7; jne 0x69f8bf, the region screen's GUNSLINGER test
        new("the region screen's GUNSLINGER test", 0x69F8B2, [0x80, 0x79, 0x2F, 0x07, 0x75, 0x07],
            [0xE9, 0xF9, 0x8F, 0x4F, 0x00, 0x90]),                                          // jmp 0xb988b0
    ];

    /// <summary>
    /// Lists the lobbies a character may not enter, faded and closed, and GUNSLINGER's while no
    /// event is on, through the routines <see cref="BuildChaosSection"/> has written.
    /// </summary>
    internal static void GreyLockedLobbies(PeImage image)
    {
        RequireInChaos(image, LockedLobbyVa, LockedLobbyStub, "the lobby menu's routines");
        RequireInChaos(image, ClosedNumberVa, ClosedNumberStub, "the closed plate's number");
        RequireInChaos(image, RegionEffectVa, RegionEffectStub, "the region screen's test");
        RequireInChaos(image, EmptyStringVa, [0], "the empty string");
        PatchSites(image, LobbySites);
    }
}
