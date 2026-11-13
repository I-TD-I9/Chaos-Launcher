using System.Buffers.Binary;

namespace Chaos.Client;

public static partial class ClientPatcher
{
    // The client does only a few things for a card because its number is on a list in its code.
    // sub_59efe0 holds 84 numbers whose face-up cards have an activated effect to click; the
    // hand mask in sub_5e5200 offers a monster's summon of its own from the hand, with
    // sub_5eb0d0's Tribute test; and some response windows admit a card only by its number: the
    // filter in sub_5db110, the first link of kind 5 at 0x5bc863, the Damage Step list in
    // sub_59f510, the hand list in sub_59f460 and the Flip list in sub_59e5a0. Konami's lists stop
    // at the cards released before the service closed.
    //
    // Eight places in those functions jump instead to a routine in the zeros at the end of .text.
    // It looks the card's number up in a table of the later cards (CardListTable) and answers yes
    // for the lists the card is on; for any other card it runs the instructions it replaced and
    // goes back, so the client does for those exactly what it did. A ninth place and the check at
    // ProcHeldVa keep the hand menu from offering a procedure the server knows cannot be used
    // (ProcHeldStub).
    //
    // In the zeros the routine goes first, the map from a window kind to its bit next, then the
    // table, which ends before the check at ProcHeldVa; the display stub of LoadWrapperFirst comes
    // after the check.
    internal const uint CardStubVa = 0x6EC7A0;
    internal const uint KindMapVa = 0x6EC920;
    internal const uint CardTableVa = 0x6EC960;
    internal const uint ProcHeldVa = 0x6ECF60;

    /// <summary>One place in the client's code that jumps to the routine.</summary>
    /// <param name="Name">The function the place is in.</param>
    /// <param name="Va">Where the replaced instructions start.</param>
    /// <param name="Original">The client's bytes there.</param>
    /// <param name="Patched">The jump to the routine, and nops for what is left of the instruction it cuts.</param>
    internal sealed record CardListSite(string Name, uint Va, byte[] Original, byte[] Patched);

    internal static readonly CardListSite[] CardListSites =
    [
        // push esi; mov esi, [esp+8], at the top of the activated effects list
        new("sub_59efe0", 0x59EFE0, [0x56, 0x8B, 0x74, 0x24, 0x08], [0xE9, 0xE2, 0xD7, 0x14, 0x00]),  // jmp 0x6ec7c7
        // mov eax, [esp+4]; push eax, at the top of the hand list
        new("sub_59f460", 0x59F460, [0x8B, 0x44, 0x24, 0x04, 0x50], [0xE9, 0x7F, 0xD3, 0x14, 0x00]),  // jmp 0x6ec7e4
        // the same, at the top of the Flip list
        new("sub_59e5a0", 0x59E5A0, [0x8B, 0x44, 0x24, 0x04, 0x50], [0xE9, 0x5C, 0xE2, 0x14, 0x00]),  // jmp 0x6ec801
        // push esi; mov esi, [esp+8], at the top of the Damage Step list
        new("sub_59f510", 0x59F510, [0x56, 0x8B, 0x74, 0x24, 0x08], [0xE9, 0x09, 0xD3, 0x14, 0x00]),  // jmp 0x6ec81e
        // mov ecx, [esp+0xc]; push esi, at the top of the response window's filter
        new("sub_5db110", 0x5DB110, [0x8B, 0x4C, 0x24, 0x0C, 0x56], [0xE9, 0x45, 0x17, 0x11, 0x00]),  // jmp 0x6ec85a
        // mov dx, [edi]; push edx; call 0x402150, where the first link at kind 5 is checked against the list
        new("sub_5bc6d0", 0x5BC863, [0x66, 0x8B, 0x17, 0x52, 0xE8, 0xE4, 0x58, 0xE4, 0xFF],
            [0xE9, 0x27, 0x00, 0x13, 0x00, 0x90, 0x90, 0x90, 0x90]),                                   // jmp 0x6ec88f
        // mov edi, [esp+0x1c]; push edi, before the hand mask's Tribute test
        new("sub_5e5200", 0x5E5355, [0x8B, 0x7C, 0x24, 0x1C, 0x57], [0xE9, 0x53, 0x75, 0x10, 0x00]),  // jmp 0x6ec8ad
        // cmp eax, 0x5f0; jne 0x5e544d, where the mask gives 0x5f0 the Special Summon kinds beside its Normal Summon
        new("sub_5e5200", 0x5E53FF, [0x3D, 0xF0, 0x05, 0x00, 0x00, 0x75, 0x47],
            [0xE9, 0xF5, 0x74, 0x10, 0x00, 0x90, 0x90]),                                               // jmp 0x6ec8f9
        // mov ebx, 0xc00, where the mask gives the two Special Summon kinds to a card sub_5a0a40
        // lists; the jmp 0x5e544d after it stays as it is and no longer runs
        new("sub_5e5200", 0x5E5390, [0xBB, 0x00, 0x0C, 0x00, 0x00], [0xE9, 0xF0, 0x7B, 0x10, 0x00]),  // jmp 0x6ecf85
    ];

    // The routine, 376 bytes from CardStubVa, with the listing beside the bytes. Its table and
    // kind map are read at KindMapVa and CardTableVa. The entry points are look (the lookup, which
    // every other part calls), h_act, h_hand, h_flip, h_ds, h_filt, h_k5, h_proc_a and h_proc_b.
    internal static ReadOnlySpan<byte> CardListStub =>
    [
        // look: eax = the card's id in the client's list of cards; eax = its flags, 0 if it is
        // not in the table. It changes only eax, ecx and edx.
        0x50,                                  // push eax
        0xE8, 0xAA, 0x59, 0xD1, 0xFF,          // call 0x402150, which turns the id into the card's number
        0x83, 0xC4, 0x04,                      // add esp, 4
        0x0F, 0xB7, 0xD0,                      // movzx edx, ax
        0xB9, 0x60, 0xC9, 0x6E, 0x00,          // mov ecx, 0x6ec960, the table
        // look_l: the table is in order, so the scan stops at the first number that is not smaller
        0x0F, 0xB7, 0x01,                      // movzx eax, word ptr [ecx]
        0x3B, 0xC2,                            // cmp eax, edx
        0x73, 0x05,                            // jae look_e
        0x83, 0xC1, 0x04,                      // add ecx, 4
        0xEB, 0xF4,                            // jmp look_l
        // look_e: the terminator, 0xffff, stops it too
        0x75, 0x05,                            // jne look_z
        0x0F, 0xB7, 0x41, 0x02,                // movzx eax, word ptr [ecx + 2]
        0xC3,                                  // ret
        // look_z:
        0x33, 0xC0,                            // xor eax, eax
        0xC3,                                  // ret

        // h_act: the top of sub_59efe0
        0x8B, 0x44, 0x24, 0x04,                // mov eax, dword ptr [esp + 4]
        0xE8, 0xD0, 0xFF, 0xFF, 0xFF,          // call look
        0xA8, 0x01,                            // test al, 1, the Act list
        0x74, 0x06,                            // je 0x6ec7da
        0xB8, 0x01, 0x00, 0x00, 0x00,          // mov eax, 1
        0xC3,                                  // ret
        0x56,                                  // push esi, as the client does
        0x8B, 0x74, 0x24, 0x08,                // mov esi, dword ptr [esp + 8]
        0xE9, 0x01, 0x28, 0xEB, 0xFF,          // jmp 0x59efe5

        // h_hand: the top of sub_59f460
        0x8B, 0x44, 0x24, 0x04,                // mov eax, dword ptr [esp + 4]
        0xE8, 0xB3, 0xFF, 0xFF, 0xFF,          // call look
        0xA8, 0x02,                            // test al, 2, the Hand list
        0x74, 0x06,                            // je 0x6ec7f7
        0xB8, 0x01, 0x00, 0x00, 0x00,          // mov eax, 1
        0xC3,                                  // ret
        0x8B, 0x44, 0x24, 0x04,                // mov eax, dword ptr [esp + 4]
        0x50,                                  // push eax
        0xE9, 0x64, 0x2C, 0xEB, 0xFF,          // jmp 0x59f465

        // h_flip: the top of sub_59e5a0
        0x8B, 0x44, 0x24, 0x04,                // mov eax, dword ptr [esp + 4]
        0xE8, 0x96, 0xFF, 0xFF, 0xFF,          // call look
        0xA8, 0x04,                            // test al, 4, the Flip list
        0x74, 0x06,                            // je 0x6ec814
        0xB8, 0x01, 0x00, 0x00, 0x00,          // mov eax, 1
        0xC3,                                  // ret
        0x8B, 0x44, 0x24, 0x04,                // mov eax, dword ptr [esp + 4]
        0x50,                                  // push eax
        0xE9, 0x87, 0x1D, 0xEB, 0xFF,          // jmp 0x59e5a5

        // h_ds: the top of sub_59f510, the Damage Step list; the kind is its second argument
        0x8B, 0x44, 0x24, 0x04,                // mov eax, dword ptr [esp + 4]
        0xE8, 0x79, 0xFF, 0xFF, 0xFF,          // call look
        0xA8, 0x10,                            // test al, 0x10, DamageStepAnyKind
        0x75, 0x1F,                            // jne h_ds_y
        0x8B, 0x4C, 0x24, 0x08,                // mov ecx, dword ptr [esp + 8]
        0x83, 0xF9, 0x3F,                      // cmp ecx, 0x3f
        0x77, 0x1C,                            // ja h_ds_o
        0x0F, 0xB6, 0x89, 0x20, 0xC9, 0x6E, 0x00, // movzx ecx, byte ptr [ecx + 0x6ec920], the kind's bit
        0x83, 0xF9, 0x0B,                      // cmp ecx, 0xb
        0x72, 0x10,                            // jb h_ds_o, only the Damage Step kinds' bits count here
        0x83, 0xF9, 0x0F,                      // cmp ecx, 0xf
        0x77, 0x0B,                            // ja h_ds_o
        0x0F, 0xA3, 0xC8,                      // bt eax, ecx
        0x73, 0x06,                            // jae h_ds_o
        // h_ds_y:
        0xB8, 0x01, 0x00, 0x00, 0x00,          // mov eax, 1
        0xC3,                                  // ret
        // h_ds_o:
        0x56,                                  // push esi
        0x8B, 0x74, 0x24, 0x08,                // mov esi, dword ptr [esp + 8]
        0xE9, 0xBB, 0x2C, 0xEB, 0xFF,          // jmp 0x59f515

        // h_filt: the top of sub_5db110, the response window's filter; the kind is its third argument
        0x8B, 0x4C, 0x24, 0x0C,                // mov ecx, dword ptr [esp + 0xc]
        0x83, 0xF9, 0x3F,                      // cmp ecx, 0x3f
        0x77, 0x22,                            // ja h_filt_o
        0x0F, 0xB6, 0x89, 0x20, 0xC9, 0x6E, 0x00, // movzx ecx, byte ptr [ecx + 0x6ec920], the kind's bit
        0x83, 0xF9, 0x0F,                      // cmp ecx, 0xf
        0x77, 0x16,                            // ja h_filt_o
        0x8B, 0x44, 0x24, 0x08,                // mov eax, dword ptr [esp + 8], the card
        0x51,                                  // push ecx
        0xE8, 0x27, 0xFF, 0xFF, 0xFF,          // call look
        0x59,                                  // pop ecx
        0x0F, 0xA3, 0xC8,                      // bt eax, ecx
        0x73, 0x06,                            // jae h_filt_o
        0xB8, 0x01, 0x00, 0x00, 0x00,          // mov eax, 1
        0xC3,                                  // ret
        // h_filt_o:
        0x8B, 0x4C, 0x24, 0x0C,                // mov ecx, dword ptr [esp + 0xc]
        0x56,                                  // push esi
        0xE9, 0x86, 0xE8, 0xEE, 0xFF,          // jmp 0x5db115

        // h_k5: 0x5bc863 in sub_5bc6d0, a first link at kind 5; edi points at the link
        0x0F, 0xB7, 0x07,                      // movzx eax, word ptr [edi]
        0xE8, 0x09, 0xFF, 0xFF, 0xFF,          // call look
        0xA8, 0x20,                            // test al, 0x20, Kind5
        0x0F, 0x85, 0x01, 0x00, 0xED, 0xFF,    // jne 0x5bc8a0, which accepts it
        0x66, 0x8B, 0x17,                      // mov dx, word ptr [edi]
        0x52,                                  // push edx
        0xE8, 0xA8, 0x58, 0xD1, 0xFF,          // call 0x402150
        0xE9, 0xBF, 0xFF, 0xEC, 0xFF,          // jmp 0x5bc86c

        // h_proc_a: 0x5e5355 in sub_5e5200, the hand mask's Tribute test of the summon
        0x8B, 0x7C, 0x24, 0x1C,                // mov edi, dword ptr [esp + 0x1c]
        0x57,                                  // push edi
        0x56,                                  // push esi
        0xE8, 0x18, 0xE8, 0xEF, 0xFF,          // call 0x5eb0d0
        0x83, 0xC4, 0x08,                      // add esp, 8
        0x85, 0xC0,                            // test eax, eax
        0x75, 0x35,                            // jne h_proc_a_b, the test passes: the mask goes on as before
        0x8B, 0xC5,                            // mov eax, ebp, the card
        0xE8, 0xDA, 0xFE, 0xFF, 0xFF,          // call look
        0xA8, 0x08,                            // test al, 8, the Proc list
        0x74, 0x28,                            // je h_proc_a_z
        // sub_5eb0d0 also fails for a card a face-up Prohibition names (0x5eb119 to 0x5eb12c),
        // and then the client offers no summon at all, so this asks sub_5a5640 the same way.
        0x0F, 0xB7, 0xC5,                      // movzx eax, bp
        0x25, 0xFF, 0x0F, 0x00, 0x00,          // and eax, 0xfff
        0x6A, 0xFF,                            // push -1
        0x50,                                  // push eax
        0xE8, 0x66, 0x8D, 0xEB, 0xFF,          // call 0x5a5640
        0x83, 0xC4, 0x08,                      // add esp, 8
        0x85, 0xC0,                            // test eax, eax
        0x75, 0x11,                            // jne h_proc_a_z
        0x0F, 0xB7, 0xC5,                      // movzx eax, bp
        0x89, 0x44, 0x24, 0x18,                // mov dword ptr [esp + 0x18], eax, as 0x5e5373 keeps the card
        0xE9, 0x73, 0x06, 0x00, 0x00,          // jmp 0x6ecf60, proc_a, which gives the two Special Summon kinds
        0x90, 0x90, 0x90, 0x90, 0x90,          // nop x5
        // h_proc_a_z:
        0x33, 0xC0,                            // xor eax, eax
        // h_proc_a_b:
        0xE9, 0x6A, 0x8A, 0xEF, 0xFF,          // jmp 0x5e5363, the client's own test of eax

        // h_proc_b: 0x5e53ff in sub_5e5200, where the Special Summon kinds join 0x5f0's Normal Summon
        0x3D, 0xF0, 0x05, 0x00, 0x00,          // cmp eax, 0x5f0
        0x0F, 0x84, 0x02, 0x8B, 0xEF, 0xFF,    // je 0x5e5406
        0x8B, 0xC5,                            // mov eax, ebp
        0xE8, 0x95, 0xFE, 0xFF, 0xFF,          // call look
        0xA8, 0x08,                            // test al, 8, the Proc list
        0x0F, 0x85, 0x62, 0x06, 0x00, 0x00,    // jne 0x6ecf75, proc_b, which goes on to 0x5e5406
        0xE9, 0x35, 0x8B, 0xEF, 0xFF,          // jmp 0x5e544d
    ];

    // The two places in the routine that 0.5.2 and the versions before it wrote otherwise, by their
    // offset in it: h_proc_a gave the Special Summon kinds itself, mov ebx, 0xc00 and jmp 0x5e544d,
    // and h_proc_b's jne went straight to 0x5e5406. A client one of them patched is patched again.
    internal static readonly (int Offset, byte[] Bytes)[] EarlierRoutine =
    [
        (0x148, [0xBB, 0x00, 0x0C, 0x00, 0x00, 0xE9, 0x5B, 0x8B, 0xEF, 0xFF]),
        (0x16D, [0x0F, 0x85, 0xF3, 0x8A, 0xEF, 0xFF]),
    ];

    // The hand mask gives a card on the Proc list its two Special Summon kinds with no condition
    // of its own, on three paths: h_proc_a when sub_5eb0d0's Tribute test fails, h_proc_b when it passes,
    // and Konami's own mov ebx, 0xc00 at 0x5e5390 for the cards sub_5a0a40 lists as Special Summon
    // only, Inferno and the other Graveyard banishers among them. Konami's arms in sub_5eb0d0 offer
    // their own cards' procedures only when the summon can be made. The later cards have no arm,
    // so the server, which knows from the engine which procedures can be used, says which cannot:
    // event 0x0D sets bit 5 + i of the side's seat word, [side*0xe14 + 0xb87204], for the card at
    // hand index i (its handler writes any bit and draws nothing, 0x40a8c6-0x40a8f2). The client's
    // own reads and writes of the word use bits 1 to 4 and 16 to 24, none of 5 to 15, and event
    // 0x02 clears only bits 16 to 31 (0x5b011b-0x5b013b). All three paths come here: a set bit
    // leaves the two kinds out, and a clear one, or a hand index of 11 or more, gives them as
    // before, so the client offers what it always did until the server sets a bit.
    //
    // edi is the hand index on every path, from h_proc_a's mov edi, [esp+0x1c], and nothing
    // writes it up to 0x5e53ff; esi is the side and ebp the card. At 0x5e5390 ebx is still 0
    // (0x5e5210) and eax, ecx and edx hold nothing that is read again.
    internal static ReadOnlySpan<byte> ProcHeldStub =>
    [
        // proc_a: from h_proc_a, where the Tribute test failed
        0xE8, 0x40, 0x00, 0x00, 0x00,          // call held
        0x0F, 0x82, 0x87, 0xF9, 0xFF, 0xFF,    // jb 0x6ec8f2, h_proc_a_z: no summon, as Konami's fail path
        0xBB, 0x00, 0x0C, 0x00, 0x00,          // mov ebx, 0xc00, the two Special Summon kinds
        0xE9, 0xD8, 0x84, 0xEF, 0xFF,          // jmp 0x5e544d, where the lock tests clear them

        // proc_b: from h_proc_b, where the Tribute test passed for a card sub_5a0a40 does not list
        0xE8, 0x2B, 0x00, 0x00, 0x00,          // call held
        0x0F, 0x82, 0xCD, 0x84, 0xEF, 0xFF,    // jb 0x5e544d, with the Normal Summon and the Set alone
        0xE9, 0x81, 0x84, 0xEF, 0xFF,          // jmp 0x5e5406, or bh, 0xc

        // proc_c: from 0x5e5390, where the Tribute test passed for a card sub_5a0a40 lists
        0x89, 0xE8,                            // mov eax, ebp
        0xE8, 0x14, 0xF8, 0xFF, 0xFF,          // call look
        0xA8, 0x08,                            // test al, 8, the Proc list
        0x74, 0x0B,                            // je 0x6ecf9b, one of Konami's own cards
        0xE8, 0x10, 0x00, 0x00, 0x00,          // call held
        0x0F, 0x82, 0xFF, 0x83, 0xEF, 0xFF,    // jb 0x5e539a, push 0xd and call 0x46ad20, the notice
                                               // Konami's fail path gives such a card (0x5e54a5)
        0xBB, 0x00, 0x0C, 0x00, 0x00,          // mov ebx, 0xc00
        0xE9, 0xA8, 0x84, 0xEF, 0xFF,          // jmp 0x5e544d

        // held: the carry flag is bit 5 + edi of the side's seat word, and clear for an index of 11
        // or more. It changes only eax and ecx.
        0x83, 0xFF, 0x0B,                      // cmp edi, 0xb
        0x73, 0x17,                            // jae 0x6ecfc1, with the carry clear
        0x89, 0xF0,                            // mov eax, esi
        0x83, 0xE0, 0x01,                      // and eax, 1
        0x69, 0xC0, 0x14, 0x0E, 0x00, 0x00,    // imul eax, eax, 0xe14
        0x8B, 0x80, 0x04, 0x72, 0xB8, 0x00,    // mov eax, dword ptr [eax + 0xb87204], the seat word
        0x8D, 0x4F, 0x05,                      // lea ecx, [edi + 5]
        0x0F, 0xA3, 0xC8,                      // bt eax, ecx
        0xC3,                                  // ret
    ];

    /// <summary>
    /// The bytes the routine, the kind map and the table take in the zeros from
    /// <see cref="CardStubVa"/> to <see cref="ProcHeldVa"/>, with zeros between and after.
    /// </summary>
    internal static byte[] CardListCave()
    {
        var table = CardListTable.EncodeTable();
        if (table.Length > CardListTable.Capacity)
            throw new PatchException($"the table of {CardListTable.Entries.Count} cards ends at 0x{CardTableVa + table.Length:x}, past 0x{ProcHeldVa:x} where the procedure check begins");
        var cave = new byte[ProcHeldVa - CardStubVa];
        CardListStub.CopyTo(cave);
        CardListTable.EncodeKindMap().CopyTo(cave, KindMapVa - CardStubVa);
        table.CopyTo(cave, CardTableVa - CardStubVa);
        return cave;
    }

    /// <summary>
    /// Whether <paramref name="found"/>, the bytes from <see cref="CardStubVa"/> to
    /// <see cref="ProcHeldVa"/>, are what an earlier patch wrote there: the routine, as this patch
    /// or an older one (<see cref="EarlierRoutine"/>) writes it, the kind map, a table in order
    /// with its terminator, and zeros between and after.
    /// </summary>
    static bool IsEarlierCave(ReadOnlySpan<byte> found)
    {
        var routine = CardListStub.ToArray();
        var earlier = (byte[])routine.Clone();
        foreach (var (offset, bytes) in EarlierRoutine)
            bytes.CopyTo(earlier, offset);
        var head = found[..routine.Length];
        if (!head.SequenceEqual(routine) && !head.SequenceEqual(earlier))
            return false;
        int map = (int)(KindMapVa - CardStubVa), table = (int)(CardTableVa - CardStubVa);
        if (found[routine.Length..map].ContainsAnyExcept((byte)0) ||
            !found[map..(map + CardListTable.KindCount)].SequenceEqual(CardListTable.EncodeKindMap()) ||
            found[(map + CardListTable.KindCount)..table].ContainsAnyExcept((byte)0))
            return false;
        int last = -1;
        for (int at = table; at + 4 <= found.Length; at += 4)
        {
            int number = BinaryPrimitives.ReadUInt16LittleEndian(found[at..]);
            if (number == CardListTable.Terminator)
                return BinaryPrimitives.ReadUInt16LittleEndian(found[(at + 2)..]) == 0 &&
                       !found[(at + 4)..].ContainsAnyExcept((byte)0);
            if (number <= last)
                return false;
            last = number;
        }
        return false;
    }

    internal static void ExtendCardLists(PeImage image)
    {
        var cave = CardListCave();
        // Zeros, or the routine and tables from an earlier patch, this version's or an older one's.
        var found = image.Read(CardStubVa, cave.Length);
        bool written = found.SequenceEqual(cave);
        if (!written && found.ContainsAnyExcept((byte)0) && !IsEarlierCave(found))
            throw new PatchException($"expected zeros at 0x{CardStubVa:x} for {cave.Length} bytes, found bytes from 0x{CardStubVa + (uint)found.IndexOfAnyExcept((byte)0):x}");

        // Zeros, or the procedure check from an earlier patch.
        var check = image.Read(ProcHeldVa, ProcHeldStub.Length);
        bool checkWritten = check.SequenceEqual(ProcHeldStub);
        if (!checkWritten && check.ContainsAnyExcept((byte)0))
            throw new PatchException($"expected zeros at 0x{ProcHeldVa:x} for {ProcHeldStub.Length} bytes, found {Convert.ToHexString(check)}");

        // Each place still has the client's instructions or already has the jump.
        foreach (var site in CardListSites)
        {
            var bytes = image.Read(site.Va, site.Original.Length);
            if (!bytes.SequenceEqual(site.Patched) && !bytes.SequenceEqual(site.Original))
                throw new PatchException($"expected {Convert.ToHexString(site.Original)} in {site.Name} at 0x{site.Va:x}, found {Convert.ToHexString(bytes)}");
        }

        EnsureInText(image, CardStubVa, (int)(ProcHeldVa - CardStubVa) + ProcHeldStub.Length);
        if (!written)
            image.Write(CardStubVa, cave);
        if (!checkWritten)
            image.Write(ProcHeldVa, ProcHeldStub);
        foreach (var site in CardListSites)
            image.Write(site.Va, site.Patched);
    }
}
