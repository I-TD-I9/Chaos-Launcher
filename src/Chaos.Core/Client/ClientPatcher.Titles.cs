namespace Chaos.Client;

public static partial class ClientPatcher
{
    // A character wears one title, the u16 at +0xca of its record, and the client turns it into
    // text in one routine, sub_678150, and into the lobby plate's colour in another, sub_6783a0.
    // Numbers 1 to 0x92f are card names; anything else is looked up in Konami's table of eleven
    // at 0x71b620, and a number it doesn't hold reads "Unknown", drawn near-black on the plate.
    // Nothing else in the client treats a title as anything but a number to copy, or to compare
    // with GAME MASTER's 0xf001, so the server's own titles take 0x1000 to 0x1fff, between the
    // cards and Konami's.
    //
    // Their names and plate colours come from bc\chaos_titles.bin, which the launcher writes
    // before it starts the game (Titles.TitleNames). The first title the client draws loads the
    // file, once, through the same file object CARD_ID.bin is read with (sub_4021b0, which looks
    // in each folder patch.txt names, bc first, then sub_5771c0 for its bytes, or 0 when there
    // are none):
    //   +0x00 "CHAOSTTL", +0x08 u32 count, +0x0c u32 0, then count records of 64 bytes, record i
    //   being title 0x1000 + i: u32 COLORREF (0xffffffff for the client's own near-black), then
    //   the name, at most 59 bytes and a zero.
    // A missing or broken file, a number past the count or a record with no name leaves the
    // client as it was: "Unknown", near-black.
    //
    // In sub_678150, 0x67826b is where a number that is no card goes to Konami's table: xor esi,
    // esi; mov eax, 0x71b620. It jumps to OwnTitleNameVa instead, which for one of ours points esi
    // at the record's name and goes on at 0x6781ef, where a card's name is made into the result
    // from esi, and otherwise does what the two instructions did and goes on at 0x678272. In
    // sub_6783a0, the and eax, 0xffff at 0x6783b7 jumps to OwnTitleColourVa, which returns the
    // record's colour for one of ours and otherwise goes back to 0x6783bc. Only the plate shows
    // the colour: the Profile (0x69c126) and "Your title has changed!" (0x6299ba) draw every title
    // in a colour of their own.
    internal const uint OwnTitleNameVa = 0xB98A00;
    internal const uint OwnTitleColourVa = 0xB98A2F;
    internal const uint OwnTitleRecordVa = 0xB98A65;
    internal const uint TitlesFileNameVa = 0xB98AE1;

    // .chaos is not writable, so the routines keep what they load at the end of .data, past
    // 0xb93070 where the client's own data ends in memory (nothing in it reads or writes there):
    // the file object at 0xb93070, 0x30 bytes as CARD_ID.bin's at 0x7244d0; the file's bytes, or
    // 0, at 0xb930a0; and at 0xb930a4 a byte set once the load has been tried. .data is made to
    // reach 0xb93100, which leaves the rest of that room for the patches to come; it still ends in
    // the page before .rsrc's, so no section moves.
    internal const uint TitlesStateVa = 0xB93070;
    internal const int TitlesStateLength = 0x90;

    // The routines, 225 bytes from OwnTitleNameVa, and the file's name after them.
    internal static ReadOnlySpan<byte> OwnTitlesStub =>
    [
        // name: from 0x67826b, with cx the title, as sub_678150 loads it at 0x6781c8
        0x0F, 0xB7, 0xC1,                      // movzx eax, cx
        0x2D, 0x00, 0x10, 0x00, 0x00,          // sub eax, 0x1000
        0x3D, 0x00, 0x10, 0x00, 0x00,          // cmp eax, 0x1000
        0x73, 0x14,                            // jae name_konami, not one of ours
        0x51,                                  // push ecx, the title, for Konami's table
        0x50,                                  // push eax, its record's index
        0xE8, 0x4F, 0x00, 0x00, 0x00,          // call record
        0x59,                                  // pop ecx
        0x85, 0xC0,                            // test eax, eax
        0x74, 0x08,                            // je name_konami, no name: "Unknown", as before
        0x8D, 0x70, 0x04,                      // lea esi, [eax + 4], the name
        0xE9, 0xCC, 0xF7, 0xAD, 0xFF,          // jmp 0x6781ef, made into the result as a card's name is
        // name_konami:
        0x33, 0xF6,                            // xor esi, esi, as the client does
        0xB8, 0x20, 0xB6, 0x71, 0x00,          // mov eax, 0x71b620
        0xE9, 0x43, 0xF8, 0xAD, 0xFF,          // jmp 0x678272, Konami's table

        // colour: from 0x6783b7, with eax the title from [esp + 4]
        0x25, 0xFF, 0xFF, 0x00, 0x00,          // and eax, 0xffff, as the client does
        0x8D, 0x90, 0x00, 0xF0, 0xFF, 0xFF,    // lea edx, [eax - 0x1000]
        0x81, 0xFA, 0x00, 0x10, 0x00, 0x00,    // cmp edx, 0x1000
        0x73, 0x1E,                            // jae colour_konami
        0x52,                                  // push edx
        0xE8, 0x1D, 0x00, 0x00, 0x00,          // call record
        0x85, 0xC0,                            // test eax, eax
        0x74, 0x0C,                            // je colour_default
        0x8B, 0x00,                            // mov eax, dword ptr [eax], the record's colour
        0xA9, 0x00, 0x00, 0x00, 0xFF,          // test eax, 0xff000000
        0x75, 0x03,                            // jne colour_default, the client's own
        0xC2, 0x04, 0x00,                      // ret 4
        // colour_default:
        0xB8, 0x10, 0x10, 0x10, 0x00,          // mov eax, 0x101010, what the client gives any other title
        0xC2, 0x04, 0x00,                      // ret 4
        // colour_konami:
        0xE9, 0x57, 0xF9, 0xAD, 0xFF,          // jmp 0x6783bc

        // record(index), which pops its argument: the 64-byte record of title 0x1000 + index when
        // it has a name, or 0. The first call loads the file.
        0x80, 0x3D, 0xA4, 0x30, 0xB9, 0x00, 0x00, // cmp byte ptr [0xb930a4], 0
        0x75, 0x4F,                            // jne loaded
        0xC6, 0x05, 0xA4, 0x30, 0xB9, 0x00, 0x01, // mov byte ptr [0xb930a4], 1, tried
        0x68, 0xE1, 0x8A, 0xB9, 0x00,          // push 0xb98ae1, "chaos_titles.bin"
        0xB9, 0x70, 0x30, 0xB9, 0x00,          // mov ecx, 0xb93070, the file object
        0xE8, 0x2C, 0x97, 0x86, 0xFF,          // call 0x4021b0, which reads the file
        0xB9, 0x70, 0x30, 0xB9, 0x00,          // mov ecx, 0xb93070
        0xE8, 0x32, 0xE7, 0x9D, 0xFF,          // call 0x5771c0, its bytes, or 0
        0x85, 0xC0,                            // test eax, eax
        0x74, 0x26,                            // je store
        0x8B, 0x0D, 0x7C, 0x30, 0xB9, 0x00,    // mov ecx, dword ptr [0xb9307c], its size
        0x83, 0xE9, 0x10,                      // sub ecx, 0x10
        0x72, 0x19,                            // jb bad, shorter than the header
        0x81, 0x38, 0x43, 0x48, 0x41, 0x4F,    // cmp dword ptr [eax], "CHAO"
        0x75, 0x11,                            // jne bad
        0x81, 0x78, 0x04, 0x53, 0x54, 0x54, 0x4C, // cmp dword ptr [eax + 4], "STTL"
        0x75, 0x08,                            // jne bad
        0xC1, 0xE9, 0x06,                      // shr ecx, 6, the whole records the file holds
        0x39, 0x48, 0x08,                      // cmp dword ptr [eax + 8], ecx
        0x76, 0x02,                            // jbe store, the count fits
        // bad:
        0x33, 0xC0,                            // xor eax, eax
        // store:
        0xA3, 0xA0, 0x30, 0xB9, 0x00,          // mov dword ptr [0xb930a0], eax
        // loaded:
        0xA1, 0xA0, 0x30, 0xB9, 0x00,          // mov eax, dword ptr [0xb930a0]
        0x85, 0xC0,                            // test eax, eax
        0x74, 0x18,                            // je done
        0x8B, 0x4C, 0x24, 0x04,                // mov ecx, dword ptr [esp + 4], the index
        0x3B, 0x48, 0x08,                      // cmp ecx, dword ptr [eax + 8]
        0x73, 0x0D,                            // jae none, past the count
        0xC1, 0xE1, 0x06,                      // shl ecx, 6
        0x8D, 0x44, 0x08, 0x10,                // lea eax, [eax + ecx + 0x10]
        0x80, 0x78, 0x04, 0x00,                // cmp byte ptr [eax + 4], 0
        0x75, 0x02,                            // jne done, it has a name
        // none:
        0x33, 0xC0,                            // xor eax, eax
        // done:
        0xC2, 0x04, 0x00,                      // ret 4

        // At TitlesFileNameVa, looked for under each line of patch.txt.
        0x63, 0x68, 0x61, 0x6F, 0x73, 0x5F, 0x74, 0x69, 0x74,
        0x6C, 0x65, 0x73, 0x2E, 0x62, 0x69, 0x6E, 0x00, // "chaos_titles.bin"
    ];

    internal static readonly SitePatch[] OwnTitleSites =
    [
        // xor esi, esi; mov eax, 0x71b620, before sub_678150 walks Konami's titles
        new("the title's name", 0x67826B, [0x33, 0xF6, 0xB8, 0x20, 0xB6, 0x71, 0x00],
            [0xE9, 0x90, 0x07, 0x52, 0x00, 0x90, 0x90]),                                    // jmp 0xb98a00
        // and eax, 0xffff, at the top of sub_6783a0
        new("the title's colour", 0x6783B7, [0x25, 0xFF, 0xFF, 0x00, 0x00], [0xE9, 0x73, 0x06, 0x52, 0x00]), // jmp 0xb98a2f
    ];

    /// <summary>
    /// Has the client name the server's own titles, 0x1000 to 0x1fff, and colour them on the lobby
    /// plate, from the file the launcher writes into bc, through the routines
    /// <see cref="BuildChaosSection"/> has written.
    /// </summary>
    internal static void NameOwnTitles(PeImage image)
    {
        RequireInChaos(image, OwnTitleNameVa, OwnTitlesStub, "the titles' routines");
        EnsureInData(image, TitlesStateVa, TitlesStateLength);
        PatchSites(image, OwnTitleSites);
    }

    /// <summary>
    /// Makes .data reach over the <paramref name="length"/> bytes at <paramref name="va"/>, which
    /// must lie past its bytes on disk, where the loader gives it zeros, and before the next
    /// section. A .data that already reaches into them, other than to their end, is the client's
    /// own data there, or another patch's, and is refused.
    /// </summary>
    static void EnsureInData(PeImage image, uint va, int length)
    {
        var data = image.SectionNamed(".data");
        ulong start = (ulong)image.ImageBase + data.VirtualAddress;
        ulong end = (ulong)va + (uint)length;
        ulong next = image.Sections.Where(s => s.VirtualAddress > data.VirtualAddress)
                          .Select(s => (ulong)image.ImageBase + s.VirtualAddress)
                          .DefaultIfEmpty(ulong.MaxValue).Min();
        if (va < start + data.RawSize || end > next)
            throw new PatchException($"the {length} bytes at 0x{va:x} are not in the room at the end of .data");
        ulong reach = start + data.VirtualSize;
        if (reach == end)
            return;
        if (reach > va)
            throw new PatchException($".data reaches 0x{reach:x}, into the {length} bytes at 0x{va:x} it is to be given");
        image.SetVirtualSize(".data", (uint)(end - start));
    }
}
