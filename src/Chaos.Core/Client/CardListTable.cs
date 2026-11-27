using System.Buffers.Binary;

namespace Chaos.Client;

[Flags]
public enum CardList : ushort
{
    Act = 0x0001,
    Hand = 0x0002,
    Flip = 0x0004,
    Proc = 0x0008,
    DamageStep = 0x0010,
    // The window kinds.
    Kind5 = 0x0020,
    Kind6 = 0x0040,
    Kind7 = 0x0080,
    Kind8 = 0x0100,
    Kind10 = 0x0200,
    Kind28 = 0x0400,
    Kind13 = 0x0800,
    Kind14 = 0x1000,
    Kind19 = 0x2000,
    Kind21 = 0x4000,
    Kind52 = 0x8000,
}

public static class CardListTable
{
    internal const int Capacity = (int)(ClientPatcher.ProcHeldVa - ClientPatcher.CardTableVa);

    public static IReadOnlyDictionary<int, int> WindowBits { get; } = new Dictionary<int, int>
    {
        [5] = 5,
        [6] = 6,
        [7] = 7,
        [8] = 8,
        [10] = 9,
        [28] = 10,
        [13] = 11,
        [14] = 12,
        [19] = 13,
        [21] = 14,
        [52] = 15,
    };

    public static IReadOnlyList<(ushort Number, CardList Lists)> Entries { get; } =
    [
        (0x074B, CardList.Act),                                       // Hannibal Necromancer
        (0x078A, CardList.Flip),                                      // Sonic Jammer
        (0x0804, CardList.Proc),                                      // Toon Dark Magician Girl
        (0x0810, CardList.Act),                                       // Toon Cannon Soldier
        (0x0857, CardList.Act),                                       // XY-Dragon Cannon
        (0x0859, CardList.Act),                                       // XYZ-Dragon Cannon
        (0x0860, CardList.Act),                                       // Lekunga
        (0x0863, CardList.Kind5 | CardList.Kind6),
        (0x0873, CardList.Act),                                       // Union Rider
        (0x087A, CardList.Act),                                       // Roulette Barrel
        (0x087B, CardList.Act),                                       // Paladin of White Dragon
        (0x087D, CardList.Act),                                       // Frontline Base
        (0x0889, CardList.Act),                                       // Wave-Motion Cannon
        (0x0894, CardList.Act),                                       // XZ-Tank Cannon
        (0x0895, CardList.Act),                                       // YZ-Tank Dragon
        (0x0897, CardList.Act),                                       // Dark Paladin
        (0x08A6, CardList.Act),                                       // Skilled White Magician
        (0x08A7, CardList.Act),                                       // Skilled Dark Magician
        (0x08AB, CardList.Act),                                       // Magical Marionette
        (0x08AD, CardList.Act),                                       // Breaker the Magical Warrior
        (0x08AF, CardList.Act),                                       // Magical Scientist
        (0x08B0, CardList.Act),                                       // Royal Magical Library
        (0x08B2, CardList.Act),                                       // Tribe-Infecting Virus
        (0x08BA, CardList.Act),                                       // Pitch-Black Power Stone
        (0x08BD, CardList.Act),                                       // Mass Driver
        (0x08C1, CardList.DamageStep),                                // My Body as a Shield
        (0x08DC, CardList.Act),                                       // Guardian Ceal
        (0x08DD, CardList.Proc),                                      // Guardian Grarl
        (0x090E, CardList.Act),                                       // Sasuke Samurai #2
        (0x0919, CardList.Kind28),                                    // Desrook Archfiend
        (0x091F, CardList.Act),                                       // Legendary Flame Lord
        (0x0920, CardList.Act),                                       // Dark Master - Zorc
        (0x0927, CardList.Act),                                       // Archfiend's Oath
        (0x0935, CardList.Act),                                       // Chiron the Mage
        (0x093C, CardList.Act),                                       // Strike Ninja
        (0x0943, CardList.Act),                                       // Freed the Brave Wanderer
        (0x0947, CardList.Act),                                       // Chaosrider Gustaph
        (0x0948, CardList.Proc),                                      // Inferno
        (0x0949, CardList.Proc),                                      // Fenrir
        (0x094A, CardList.Proc),                                      // Gigantes
        (0x094B, CardList.Proc),                                      // Silpheed
        (0x094C, CardList.Act | CardList.Proc),                       // Chaos Sorcerer
        (0x094E, CardList.Act | CardList.Proc),
        (0x0967, CardList.Act | CardList.Proc),                       // Chaos Emperor Dragon - Envoy of the End
        (0x0975, CardList.Act),                                       // Stealth Bird
        (0x097D, CardList.Act),                                       // Anti-Aircraft Flower
        (0x0982, CardList.Act),                                       // Levia-Dragon - Daedalus
        (0x0983, CardList.Act),                                       // Orca Mega-Fortress of Darkness
        (0x099F, CardList.Act),                                       // Abyss Soldier
        (0x09B0, CardList.Act),                                       // The Agent of Judgment - Saturn
        (0x09B2, CardList.Act),                                       // The Agent of Creation - Venus
        (0x09B9, CardList.Act),                                       // Avatar of The Pot
        (0x09BD, CardList.Act),                                       // Blowback Dragon
        (0x09C4, CardList.Act),                                       // Arcane Archer of the Forest
        (0x09C5, CardList.Act),                                       // Lady Ninja Yae
        (0x09C9, CardList.Act | CardList.Proc),                       // Archlord Zerato
        (0x09DD, CardList.Proc),                                      // Mazera DeVille
        (0x09E1, CardList.Hand),
        (0x09F3, CardList.Act),                                       // Sand Gambler
        (0x09F4, CardList.Act),                                       // 3-Hump Lacooda
        (0x09FF, CardList.Act),                                       // Man-Thro' Tro'
        (0x0A1E, CardList.Act),
        (0x0A21, CardList.Act | CardList.Proc),                       // Blue-Eyes Shining Dragon
        (0x0A25, CardList.Act | CardList.Proc),                       // Sorcerer of Dark Magic
        (0x0A26, CardList.Proc),                                      // Andro Sphinx
        (0x0A27, CardList.Proc),                                      // Sphinx Teleia
        (0x0A32, CardList.Act),                                       // Horus the Black Flame Dragon LV8
        (0x0A38, CardList.Act),                                       // Armed Dragon LV5
        (0x0A39, CardList.Act),                                       // Armed Dragon LV7
        (0x0A3B, CardList.Act),                                       // Red-Eyes B. Chick
        (0x0A3F, CardList.Act),                                       // Ultimate Baseball Kid
        (0x0A64, CardList.Proc),
        (0x0A76, CardList.Act),                                       // The Creator
        (0x0A77, CardList.Act),                                       // The Creator Incarnate
        (0x0A8A, CardList.Act),                                       // Gaia Soul the Combustible Collective
        (0x0A8F, CardList.Act),                                       // A-Team: Trap Disposal Unit
        (0x0A90, CardList.Act),                                       // Homunculus the Alchemic Being
        (0x0A9C, CardList.Act),                                       // Lighten the Load
        (0x0AA0, CardList.Kind19),                                    // Rare Metalmorph
        (0x0AA9, CardList.Act),
        (0x0AB0, CardList.Act),                                       // Hand of Nephthys
        (0x0AB6, CardList.Act),                                       // Catnipped Kitty
        (0x0ABD, CardList.Act),                                       // Blast Magician
        (0x0AC1, CardList.Act),                                       // Golem Sentry
        (0x0AC2, CardList.Act),                                       // Abare Ushioni
        (0x0AC3, CardList.Act),                                       // The Light - Hex-Sealed Fusion
        (0x0AC4, CardList.Act),                                       // The Dark - Hex-Sealed Fusion
        (0x0AC5, CardList.Act),                                       // The Earth - Hex-Sealed Fusion
        (0x0AC9, CardList.Act),                                       // Rescue Cat
        (0x0ACB, CardList.Act),                                       // Gatling Dragon
        (0x0ACC, CardList.Act),                                       // King Dragun
        (0x0AE6, CardList.Proc),                                      // Red-Eyes Darkness Dragon
        (0x0AE7, CardList.Act | CardList.Proc),                       // Vampire Genesis
        (0x0AE8, CardList.Act),                                       // Kaibaman
        (0x0AF6, CardList.Act),                                       // Moai Interceptor Cannons
        (0x0AF7, CardList.Proc),                                      // Megarock Dragon
        (0x0AFC, CardList.Proc),                                      // Master Monk
        (0x0AFD, CardList.Act),                                       // Guardian Statue
        (0x0AFE, CardList.Act),                                       // Medusa Worm
        (0x0B00, CardList.Act),                                       // Mid Shield Gardna
        (0x0B09, CardList.Act),                                       // Reshef the Dark Being
        (0x0B0C, CardList.Act),                                       // Elemental Hero Thunder Giant
        (0x0B15, CardList.Act),                                       // Shifting Shadows
        (0x0B26, CardList.Act | CardList.Proc),                       // Ocean Dragon Lord - Neo-Daedalus
        (0x0B28, CardList.Act),                                       // Patroid
        (0x0B2E, CardList.Proc),                                      // Cyber Dragon
        (0x0B30, CardList.Act),                                       // Dark Catapulter
        (0x0B31, CardList.Proc),                                      // Elemental Hero Bubbleman
        (0x0B38, CardList.Act),
        (0x0B3A, CardList.Act),                                       // Winged Kuriboh LV10
        (0x0B3D, CardList.Act),                                       // Spark Gun
        (0x0B42, CardList.Act),                                       // Cybernetic Magician
        (0x0B47, CardList.Act),                                       // B.E.S. Crystal Core
        (0x0B52, CardList.Act),                                       // D.D.M. - Different Dimension Master
        (0x0B62, CardList.Kind7 | CardList.Kind8 | CardList.Kind10),  // D.D. Trap Hole
        (0x0B68, CardList.Act),                                       // Arcana Knight Joker
        (0x0B7C, CardList.Act),
        (0x0B7D, CardList.Act),
        (0x0B80, CardList.Act),
        (0x0B86, CardList.DamageStep | CardList.Kind28),
        (0x0B8A, CardList.Act),
        (0x0B93, CardList.Proc),
        (0x0B94, CardList.Proc),
        (0x0B95, CardList.Proc),                                      // Familiar-Possessed - Hiita
        (0x0B96, CardList.Proc),
        (0x0BA9, CardList.Proc),
        (0x0BB1, CardList.Act | CardList.Proc),
        (0x0BB3, CardList.Kind7),
        (0x0BB5, CardList.Kind21),
        (0x0BB7, CardList.Act | CardList.Proc),
        (0x0BB8, CardList.Proc),
        (0x0BB9, CardList.Act | CardList.Proc),
        (0x0BBA, CardList.Proc),
        (0x0BBD, CardList.Act),
        (0x0BBE, CardList.Proc),
        (0x0BCA, CardList.Kind14),
        (0x0BCB, CardList.Act),
        (0x0BD8, CardList.Act | CardList.Proc),
        (0x0BDA, CardList.Proc),
        (0x0BE5, CardList.Act),
        (0x0BE8, CardList.Act),
        (0x0BF0, CardList.Kind21),
        (0x0BF3, CardList.Act),                                       // Gear Golem the Moving Fortress
    ];

    internal const ushort Terminator = 0xFFFF;

    internal const int KindCount = 64;

    internal static byte[] EncodeTable()
    {
        var table = new byte[(Entries.Count + 1) * 4];
        for (int i = 0; i < Entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 4), Entries[i].Number);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 4 + 2), (ushort)Entries[i].Lists);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(Entries.Count * 4), Terminator);
        return table;
    }

    internal static byte[] EncodeKindMap()
    {
        var map = new byte[KindCount];
        Array.Fill(map, (byte)0xFF);
        foreach (var (kind, bit) in WindowBits)
            map[kind] = (byte)bit;
        return map;
    }
}
