using System.Buffers.Binary;

namespace Chaos.Client;

/// <summary>
/// What the client does for a card, by the lists of card numbers it keeps in its code. Online it
/// only clicks an effect, offers a summon or opens a response window for a number on one of
/// these lists; any other card is passed over, and Konami's lists stop at the cards released
/// before the service closed. Each member is one list the card is added to.
/// </summary>
[Flags]
public enum CardList : ushort
{
    /// <summary>
    /// A face-up card's activated effect can be clicked: the 84 numbers sub_59efe0 knows. Without
    /// it the card sits on the field with no effect to choose.
    /// </summary>
    Act = 0x0001,

    /// <summary>A card can be activated from the hand: the list in sub_59f460.</summary>
    Hand = 0x0002,

    /// <summary>A card has a Flip effect: the list in sub_59e5a0.</summary>
    Flip = 0x0004,

    /// <summary>
    /// A monster has a summon of its own from the hand, which the client's hand menu offers beside
    /// the Normal Summon (the mask in sub_5e5200, with the Tribute test of sub_5eb0d0).
    /// </summary>
    Proc = 0x0008,

    /// <summary>
    /// A card may be played in the Damage Step whatever the window: the list in sub_59f510 that
    /// holds its cards for every kind of window.
    /// </summary>
    DamageStepAnyKind = 0x0010,

    // The window kinds. A response window opens for a card only at the kinds its number is listed
    // for: sub_5db110 filters them, sub_59f510 holds the Damage Step's own lists for 13, 14, 19,
    // 21 and 52, and the first link of kind 5 has a rule of its own at 0x5bc863. The client takes
    // a kind as a number; here each kind has a flag, and CardListTable.WindowBit maps the kinds
    // to their bits. What each kind follows is read from the cards Konami's lists put on it.

    /// <summary>The window after a monster is Summoned.</summary>
    Kind5 = 0x0020,

    /// <summary>The window after a monster is Flip Summoned.</summary>
    Kind6 = 0x0040,

    /// <summary>The window after a monster is Special Summoned.</summary>
    Kind7 = 0x0080,

    /// <summary>The window after a monster is Set.</summary>
    Kind8 = 0x0100,

    /// <summary>The window after a monster changes its battle position.</summary>
    Kind10 = 0x0200,

    /// <summary>The window after a card is sent to the Graveyard.</summary>
    Kind28 = 0x0400,

    /// <summary>A Damage Step window.</summary>
    Kind13 = 0x0800,

    /// <summary>The Damage Step window after battle damage.</summary>
    Kind14 = 0x1000,

    /// <summary>A Damage Step window.</summary>
    Kind19 = 0x2000,

    /// <summary>The Damage Step window after a monster is destroyed by battle.</summary>
    Kind21 = 0x4000,

    /// <summary>A Damage Step window.</summary>
    Kind52 = 0x8000,
}

/// <summary>
/// The cards past Konami's release that the client's lists leave out, with the lists each is
/// added to. The launcher writes them into the client as one table that the client also consults
/// wherever it checks its own lists; see <see cref="ClientPatcher.ExtendCardLists"/>.
/// </summary>
public static class CardListTable
{
    /// <summary>How many bytes the client's table may take: it must end before the procedure check, 383 cards and the terminator.</summary>
    internal const int Capacity = (int)(ClientPatcher.ProcHeldVa - ClientPatcher.CardTableVa);

    /// <summary>
    /// The bit of a card's flags that a window kind tests, by kind. A kind that is not here has
    /// no bit, so no card is added to its windows.
    /// </summary>
    public static IReadOnlyDictionary<int, int> WindowBit { get; } = new Dictionary<int, int>
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

    /// <summary>
    /// The cards by number, in the order the client scans them: ascending, each once. The names are
    /// the cards' English ones, where the server's inventory has them.
    /// </summary>
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
        (0x08C1, CardList.DamageStepAnyKind),                         // My Body as a Shield
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
        (0x094E, CardList.Act | CardList.Proc),                       // Black Luster Soldier - Envoy of the Beginning
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
        (0x0B86, CardList.DamageStepAnyKind | CardList.Kind28),
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

    /// <summary>The number that ends the table; no card has it.</summary>
    internal const ushort Terminator = 0xFFFF;

    /// <summary>The kinds the client's windows can have: the kind map holds a byte for each.</summary>
    internal const int KindCount = 64;

    /// <summary>
    /// The table as the client reads it: for each card a little-endian number and its flags, then
    /// the terminator with no flags, which also stops the scan for the number 0xffff the client
    /// gives a card it does not know.
    /// </summary>
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

    /// <summary>
    /// The map from a window kind to its bit, a byte for each of the 64 kinds, 0xff for a kind
    /// with none.
    /// </summary>
    internal static byte[] EncodeKindMap()
    {
        var map = new byte[KindCount];
        Array.Fill(map, (byte)0xFF);
        foreach (var (kind, bit) in WindowBit)
            map[kind] = (byte)bit;
        return map;
    }
}
