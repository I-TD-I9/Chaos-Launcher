using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Chaos.Client;

/// <summary>Patches the game client.</summary>
public static partial class ClientPatcher
{
    // Gate host name, the client's only server address.
    internal const uint GateHostVa = 0x71E064;
    internal const int GateHostSlot = 0x20;
    internal const string RetailGateHost = "yugi-gate01.yugioh-online.net";

    internal const uint GatePortVa = 0x67A9F2;
    internal const int RetailGatePort = 5730;
    static ReadOnlySpan<byte> GatePortOpcode => [0x66, 0xC7, 0x45, 0x14];

    internal const uint RegistryRootVa = 0x4A40DB;
    static ReadOnlySpan<byte> PushLocalMachine => [0x68, 0x02, 0x00, 0x00, 0x80];
    static ReadOnlySpan<byte> PushCurrentUser => [0x68, 0x01, 0x00, 0x00, 0x80];

    internal const uint UpdaterCopyVa = 0x4149A8;
    static ReadOnlySpan<byte> JumpIfNoUpdater => [0x75, 0x3D];
    static ReadOnlySpan<byte> JumpPastUpdater => [0xEB, 0x3D];

    internal const uint RecordlessCheckVa = 0x5E2338;
    static ReadOnlySpan<byte> RefuseRecordless => [0x0F, 0x84, 0x90, 0x00, 0x00, 0x00];
    static ReadOnlySpan<byte> AcceptRecordless => [0x0F, 0x84, 0xA1, 0x00, 0x00, 0x00];

    internal const uint DirectDrawCallVa = 0x4DB103;
    internal const uint WrapperStubVa = 0x6ECFD0;
    static ReadOnlySpan<byte> CallCoCreateInstance => [0xFF, 0x15, 0xE0, 0xD3, 0x6E, 0x00];
    static ReadOnlySpan<byte> CallWrapperStub => [0xE8, 0xC8, 0x1E, 0x21, 0x00, 0x90];
    static ReadOnlySpan<byte> WrapperStub =>
    [
        0x68, 0xE1, 0xCF, 0x6E, 0x00,
        0xFF, 0x15, 0x38, 0xD2, 0x6E, 0x00,
        0xFF, 0x25, 0xE0, 0xD3, 0x6E, 0x00,
        0x64, 0x64, 0x72, 0x61, 0x77, 0x2E, 0x64, 0x6C, 0x6C, 0x00,
    ];

    public static byte[] Patch(ReadOnlySpan<byte> client, ServerEndpoints server)
    {
        var image = new PeImage(client.ToArray());
        SetGateHost(image, server.GateHost);
        SetGatePort(image, server.GatePort);
        SetRegistryRoot(image);
        SkipUpdaterCopy(image);
        BuildChaosSection(image);
        AcceptRecordlessActivation(image);
        LoadWrapperFirst(image);
        ExtendCardLists(image);
        PointWebLinks(image);
        GreyLockedLobbies(image);
        NameOwnTitles(image);
        // Retail leaves it at zero.
        image.UpdateChecksum();
        return image.Bytes;
    }

    internal static void SetGateHost(PeImage image, string host)
    {
        if (!IsHostName(host))
            throw new PatchException($"invalid host name: {host}");
        if (host.Length > GateHostSlot - 1)
            throw new PatchException($"host name longer than {GateHostSlot - 1} characters: {host}");
        var slot = image.Read(GateHostVa, GateHostSlot);
        // Retail or already patched.
        int end = slot.IndexOf((byte)0);
        if (end <= 0 || slot[end..].ContainsAnyExcept((byte)0) || !IsHostName(Encoding.ASCII.GetString(slot[..end])))
            throw new PatchException($"no host name at 0x{GateHostVa:x}: {Convert.ToHexString(slot)}");
        var replacement = new byte[GateHostSlot];
        Encoding.ASCII.GetBytes(host.AsSpan(), replacement);
        image.Write(GateHostVa, replacement);
    }

    internal static void SetGatePort(PeImage image, int port)
    {
        if (port is < 1 or > 0xFFFF)
            throw new PatchException($"invalid port: {port}");
        var instruction = image.Read(GatePortVa, 6);
        if (!instruction[..4].SequenceEqual(GatePortOpcode))
            throw new PatchException($"expected mov word ptr [ebp+0x14], imm16 at 0x{GatePortVa:x}, found {Convert.ToHexString(instruction)}");
        Span<byte> value = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)port);
        image.Write(GatePortVa + 4, value);
    }

    internal static void SetRegistryRoot(PeImage image)
    {
        var instruction = image.Read(RegistryRootVa, PushLocalMachine.Length);
        if (instruction.SequenceEqual(PushCurrentUser))
            return;
        if (!instruction.SequenceEqual(PushLocalMachine))
            throw new PatchException($"expected push HKEY_LOCAL_MACHINE at 0x{RegistryRootVa:x}, found {Convert.ToHexString(instruction)}");
        image.Write(RegistryRootVa, PushCurrentUser);
    }

    internal static void SkipUpdaterCopy(PeImage image)
    {
        var instruction = image.Read(UpdaterCopyVa, JumpIfNoUpdater.Length);
        if (instruction.SequenceEqual(JumpPastUpdater))
            return;
        if (!instruction.SequenceEqual(JumpIfNoUpdater))
            throw new PatchException($"expected jne 0x4149e7 at 0x{UpdaterCopyVa:x}, found {Convert.ToHexString(instruction)}");
        image.Write(UpdaterCopyVa, JumpPastUpdater);
    }

    internal static void AcceptRecordlessActivation(PeImage image)
    {
        var instruction = image.Read(RecordlessCheckVa, RefuseRecordless.Length);
        if (instruction.SequenceEqual(AcceptRecordless))
            return;
        if (!instruction.SequenceEqual(RefuseRecordless))
            throw new PatchException($"expected je 0x5e23ce at 0x{RecordlessCheckVa:x}, found {Convert.ToHexString(instruction)}");
        image.Write(RecordlessCheckVa, AcceptRecordless);
    }

    internal static void LoadWrapperFirst(PeImage image)
    {
        var stub = image.Read(WrapperStubVa, WrapperStub.Length);
        if (!stub.SequenceEqual(WrapperStub))
        {
            if (stub.ContainsAnyExcept((byte)0))
                throw new PatchException($"expected zeros at 0x{WrapperStubVa:x}, found {Convert.ToHexString(stub)}");
            image.Write(WrapperStubVa, WrapperStub);
        }

        EnsureInText(image, WrapperStubVa, WrapperStub.Length);

        var call = image.Read(DirectDrawCallVa, CallCoCreateInstance.Length);
        if (call.SequenceEqual(CallWrapperStub))
            return;
        if (!call.SequenceEqual(CallCoCreateInstance))
            throw new PatchException($"expected call [0x6ed3e0] at 0x{DirectDrawCallVa:x}, found {Convert.ToHexString(call)}");
        image.Write(DirectDrawCallVa, CallWrapperStub);
    }

    static void EnsureInText(PeImage image, uint va, int length)
    {
        var text = image.GetSection(".text");
        ulong start = (ulong)image.ImageBase + text.VirtualAddress;
        ulong end = (ulong)va + (uint)length;
        if (va < start || end > start + text.RawSize)
            throw new PatchException($"{length} bytes at 0x{va:x} are outside .text");
        if (end > start + text.VirtualSize)
            image.SetVirtualSize(".text", text.RawSize);
    }

    public static bool IsHostName(string name) => name.Length is > 0 and <= 253 && HostName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z")]
    private static partial Regex HostName();
}
