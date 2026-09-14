#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Mia.Emulator;

string? frameDirectory = args.FirstOrDefault(
    argument => argument.StartsWith("--frames=", StringComparison.Ordinal))?
    ["--frames=".Length..];
bool activate = args.Contains("--activate", StringComparer.Ordinal);
bool receive = args.Contains("--receive", StringComparer.Ordinal);
bool injectProbe = args.Contains("--inject-probe", StringComparer.Ordinal);
bool injectDiscovery = args.Contains("--inject-discovery", StringComparer.Ordinal);
bool connectAfterDiscovery = args.Contains(
    "--connect-after-discovery",
    StringComparer.Ordinal);
string? objectPeerFile = args.FirstOrDefault(
    argument => argument.StartsWith(
        "--object-peer-file=",
        StringComparison.Ordinal))?["--object-peer-file=".Length..];
bool objectPeer =
    args.Contains("--object-peer", StringComparer.Ordinal) ||
    objectPeerFile is not null;
bool browsePhonebook = args.Contains("--browse-phonebook", StringComparer.Ordinal);
bool sendIrda = args.Contains("--send-irda", StringComparer.Ordinal);
bool roundTripObjectPeer = args.Contains(
    "--roundtrip-object-peer",
    StringComparer.Ordinal);
if (frameDirectory is not null)
{
    Directory.CreateDirectory(frameDirectory);
}

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

var modem = machine.Modem ??
    throw new InvalidOperationException("The ARM modem did not start.");
if (connectAfterDiscovery)
{
    machine.InfraredObjectPeer!.Enabled = false;
}
var accesses = new List<ArmModemMmioAccess>();
var linkFrames = new List<string>();
var oseSignals = new List<string>();
var linkReceiveTrace = new List<string>();
var infraredBytes = new List<byte>();
var infraredFrames = new List<byte[]>();
var irLapTrace = new List<string>();
var armToAvrDecoder = new AsicLinkFrameDecoder();
var avrToArmDecoder = new NativeLinkFrameDecoder();
var avrToArmReadDecoder = new NativeLinkFrameDecoder();
bool captureInfrared = false;
machine.InfraredPeripheral!.FrameTransmitted += frame =>
{
    if (captureInfrared && infraredFrames.Count < 256)
    {
        infraredFrames.Add(frame.ToArray());
    }
};
int linkReceiveTraceRemaining = 0;
modem.InstructionExecuting += currentModem =>
{
    if (linkReceiveTraceRemaining > 0 &&
        linkReceiveTrace.Count < 20_000)
    {
        uint tracePc = currentModem.CurrentInstructionAddress;
        linkReceiveTrace.Add(
            $"cycle={currentModem.Cycles:n0} pc=0x{tracePc:x8} " +
            $"r0=0x{currentModem.Cpu.GetGpr(0):x8} " +
            $"r1=0x{currentModem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{currentModem.Cpu.GetGpr(2):x8} " +
            $"r3=0x{currentModem.Cpu.GetGpr(3):x8} " +
            $"lr=0x{currentModem.Cpu.GetGpr(14):x8}");
        linkReceiveTraceRemaining--;
    }

    if (!captureInfrared || oseSignals.Count >= 1_000)
    {
        return;
    }

    uint pc = currentModem.CurrentInstructionAddress;
    if (pc is 0x01098080 or 0x01098100 or 0x01098108 or
        0x01098110 or 0x01098222 or 0x010994c4 &&
        irLapTrace.Count < 1_000)
    {
        irLapTrace.Add(
            $"cycle={currentModem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{currentModem.Cpu.GetGpr(0):x8} " +
            $"r1=0x{currentModem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{currentModem.Cpu.GetGpr(2):x8} " +
            $"r3=0x{currentModem.Cpu.GetGpr(3):x8}");
    }
    if (pc is 0x01001e7c or 0x01001e94 or 0x01001dda)
    {
        uint signalAddress = currentModem.Cpu.GetGpr(0);
        byte[] signal = SnapshotArmMemory(
            currentModem.Bus,
            signalAddress,
            16);
        if (signal.Length >= 2)
        {
            oseSignals.Add(
                $"receive cycle={currentModem.Cycles:n0} pc=0x{pc:x8} " +
                $"signal=0x{BitConverter.ToUInt16(signal):x4} " +
                $"lr=0x{currentModem.Cpu.GetGpr(14):x8} " +
                $"r1=0x{currentModem.Cpu.GetGpr(1):x8} " +
                $"bytes={Convert.ToHexString(signal)}");
        }
    }
    else if (pc == 0x01003040)
    {
        uint pointerAddress = currentModem.Cpu.GetGpr(0);
        byte[] pointer = SnapshotArmMemory(
            currentModem.Bus,
            pointerAddress,
            sizeof(uint));
        if (pointer.Length != sizeof(uint))
        {
            return;
        }

        uint signalAddress = BitConverter.ToUInt32(pointer);
        byte[] signal = SnapshotArmMemory(
            currentModem.Bus,
            signalAddress,
            16);
        if (signal.Length >= 2)
        {
            oseSignals.Add(
                $"send cycle={currentModem.Cycles:n0} " +
                $"pid=0x{currentModem.Cpu.GetGpr(1):x4} " +
                $"signal=0x{BitConverter.ToUInt16(signal):x4} " +
                $"lr=0x{currentModem.Cpu.GetGpr(14):x8} " +
                $"bytes={Convert.ToHexString(signal)}");
        }
    }
};
modem.Bus.MmioAccessed += access =>
{
    if (captureInfrared &&
        accesses.Count < 2_000 &&
        (access.Address is >= 0x00800100 and <= 0x0080011c or
            >= 0x00800b20 and <= 0x00800b3c ||
            ((access.Address is 0x00800508 or 0x0080050c) &&
             (access.Value & 0x00003900) != 0)))
    {
        accesses.Add(access);
    }
};
modem.Bus.Uart1ByteTransmitted += value =>
{
    if (captureInfrared &&
        armToAvrDecoder.Push(value) is { } frame)
    {
        linkFrames.Add(
            $"ARM->AVR destination=0x{frame.Destination:x2} " +
            $"source=0x{frame.Source:x2} " +
            $"payload={Convert.ToHexString(frame.Payload)}");
    }
};
modem.Bus.Uart1ByteReceived += value =>
{
    if (captureInfrared &&
        avrToArmDecoder.Push(value) is { } frame)
    {
        linkFrames.Add(
            $"AVR->ARM destination=0x{frame.Destination:x2} " +
            $"source=0x{frame.Source:x2} " +
            $"payload={Convert.ToHexString(frame.Payload)}");
    }
};
modem.Bus.InfraredByteTransmitted += value =>
{
    if (captureInfrared && infraredBytes.Count < 16_384)
    {
        infraredBytes.Add(value);
    }
};
modem.Bus.Uart1ByteRead += value =>
{
    if (captureInfrared &&
        avrToArmReadDecoder.Push(value) is { Payload: [0x85, 0xd5] })
    {
        linkReceiveTraceRemaining = 10_000;
    }
};

var center = new Contact(0x0f, 0x08);
var yes = new Contact(0x0d, 0x01);
var no = new Contact(0x0f, 0x01);
var options = new Contact(0x0d, 0x02);
var five = new Contact(0x0b, 0x02);
var right = new Contact(0x0e, 0x10, 0x07);
var down = new Contact(0x0e, 0x10, 0x0d);
var left = new Contact(0x0d, 0x10, 0x0b);

Run(105_000_000);
SaveFrame("standby");
Press("open-main-menu", center);
if (browsePhonebook || sendIrda)
{
    Press("open-phonebook", center);
    for (var index = 0; index < 7; index++)
    {
        Press($"phonebook-down-{index + 1}", down);
    }
    Press("open-phonebook-advanced", center);
    int advancedSteps = sendIrda ? 5 : 7;
    for (var index = 0; index < advancedSteps; index++)
    {
        Press($"advanced-down-{index + 1}", down);
    }
    if (sendIrda)
    {
        captureInfrared = true;
        Press("open-send-all", center, 10_000_000);
        Press("send-all-via-infrared", center, 60_000_000);
        Run(30_000_000);
    }
}
else
{
    Press("main-right-1", right);
    Press("main-right-2", right);
    Press("main-down-1", down);
    Press("main-down-2", down);
    Press("select-connect-icon", left);
    Press("open-connect", center);
    if (receive)
    {
        captureInfrared = true;
        if (objectPeer)
        {
            MiaTransferObject staged = objectPeerFile is null
                ? new(
                    "IrDA.vcf",
                    "TEXT/X-VCARD",
                    "BEGIN:VCARD\r\nVERSION:2.1\r\nFN:IrDA Peer\r\nEND:VCARD\r\n"u8
                        .ToArray())
                : new(
                    Path.GetFileName(objectPeerFile),
                    Path.GetExtension(objectPeerFile).Equals(
                        ".gif",
                        StringComparison.OrdinalIgnoreCase)
                            ? "image/gif"
                            : "application/octet-stream",
                    File.ReadAllBytes(objectPeerFile));
            machine.StageEmulatedInfraredObject(staged);
        }
        Press("receive-item", center, 30_000_000);
        if (injectProbe)
        {
            modem.Bus.QueueInfraredReceivedByte(0xc0);
            Run(2_000_000);
        }
        if (objectPeer)
        {
            if (objectPeerFile is not null)
            {
                SaveFrame("object-peer-confirmation");
                Press("accept-object-peer", yes, 20_000_000);
            }
            long remaining = objectPeerFile is null
                ? 30_000_000
                : Math.Max(
                    100_000_000,
                    new FileInfo(objectPeerFile).Length * 12_000);
            while (remaining > 0 &&
                   machine.GetInfraredTransferStatus().Phase is not
                       MiaInfraredTransferPhase.Completed and not
                       MiaInfraredTransferPhase.Failed)
            {
                const long chunk = 10_000_000;
                Run(Math.Min(chunk, remaining));
                remaining -= chunk;
            }
            SaveFrame("object-peer-result");
            if (roundTripObjectPeer &&
                machine.GetInfraredTransferStatus().Phase ==
                    MiaInfraredTransferPhase.Completed)
            {
                Run(20_000_000);
                Press("dismiss-object-peer-result", center);
                Press("return-to-standby-1", no);
                Press("return-to-standby-2", no);
                Press("roundtrip-open-main-menu", center);
                Press("roundtrip-select-fun-and-games", down);
                Press("roundtrip-open-fun-and-games", center);
                Press("roundtrip-select-my-pictures", down);
                Press("roundtrip-open-my-pictures", center);
                Run(10_000_000);
                SaveFrame("roundtrip-my-pictures-settled");
                Press("roundtrip-grid-options", options);
                Press("roundtrip-open-picture-send", five);
                Press("roundtrip-send-picture-via-infrared", center);
                long roundTripRemaining = Math.Max(
                    200_000_000,
                    new FileInfo(objectPeerFile!).Length * 12_000);
                while (roundTripRemaining > 0 &&
                       machine.GetInfraredTransferStatus()
                           .ObjectsReceivedFromHandset == 0 &&
                       machine.GetInfraredTransferStatus().Phase is not
                           MiaInfraredTransferPhase.Failed)
                {
                    const long chunk = 10_000_000;
                    Run(Math.Min(chunk, roundTripRemaining));
                    roundTripRemaining -= chunk;
                }
                SaveFrame("roundtrip-result");
                if (frameDirectory is not null &&
                    machine.GetInfraredReceivedObject() is { } roundTrip)
                {
                    File.WriteAllBytes(
                        Path.Combine(
                            frameDirectory,
                            $"roundtrip-{Path.GetFileName(roundTrip.Name)}"),
                        roundTrip.Data.ToArray());
                }
            }
        }
        if (injectDiscovery)
        {
        for (byte slot = 0; slot < 16 && !machine.IsStopped; slot++)
        {
            machine.QueueInfraredFrame(
            [
                0xff,
                0xbf,
                0x01,
                0x44, 0x33, 0x22, 0x11,
                0xff, 0xff, 0xff, 0xff,
                0x03,
                slot,
                0x00,
            ]);
            Run(75_000);
        }
        machine.QueueInfraredFrame(
        [
            0xff,
            0xbf,
            0x01,
            0x44, 0x33, 0x22, 0x11,
            0xff, 0xff, 0xff, 0xff,
            0x03,
            0xff,
            0x00,
            0x80, 0x20, 0x00,
            (byte)'C', (byte)'o', (byte)'d', (byte)'e', (byte)'x',
        ]);
        Run(5_000_000);
        if (connectAfterDiscovery)
        {
            byte[]? response = infraredFrames.FirstOrDefault(frame =>
                frame.Length >= 14 &&
                frame[0] == 0xfe &&
                (frame[1] & 0xef) == 0xaf);
            if (response is not null)
            {
                machine.QueueInfraredFrame(
                [
                    0xff, 0x93,
                    0x44, 0x33, 0x22, 0x11,
                    response[3], response[4], response[5], response[6],
                    0x22,
                    0x01, 0x01, 0x02,
                    0x82, 0x01, 0x0f,
                    0x83, 0x01, 0x01,
                    0x84, 0x01, 0x01,
                    0x85, 0x01, 0xff,
                    0x86, 0x01, 0xff,
                    0x08, 0x01, 0xff,
                ]);
                Run(5_000_000);
                if (infraredFrames.Any(frame =>
                    frame.Length >= 10 &&
                    frame[0] == 0x22 &&
                    (frame[1] & 0xef) == 0x63))
                {
                    machine.QueueInfraredFrame(
                    [
                        0x23, 0x10,
                        0x80, 0x10, 0x01, 0x00,
                    ]);
                    Run(5_000_000);
                    if (infraredFrames.Any(frame =>
                        frame.Length >= 6 &&
                        frame[0] == 0x22 &&
                        frame[2] == 0x90 &&
                        frame[4] == 0x81))
                    {
                        const string iasClass = "OBEX";
                        const string iasAttribute = "IrDA:TinyTP:LsapSel";
                        machine.QueueInfraredFrame(
                        [
                            0x23, 0x32,
                            0x00, 0x10,
                            0x84,
                            (byte)iasClass.Length,
                            .. System.Text.Encoding.ASCII.GetBytes(iasClass),
                            (byte)iasAttribute.Length,
                            .. System.Text.Encoding.ASCII.GetBytes(
                                iasAttribute),
                        ]);
                        Run(5_000_000);
                        if (infraredFrames.Any(frame =>
                            frame.Length >= 15 &&
                            frame[0] == 0x22 &&
                            frame[2] == 0x10 &&
                            frame[4] == 0x84 &&
                            frame[^5] == 0x01))
                        {
                            machine.QueueInfraredFrame(
                            [
                                0x23, 0x54,
                                0x80, 0x10, 0x02, 0x01,
                            ]);
                            Run(2_000_000);

                            machine.QueueInfraredFrame(
                            [
                                0x23, 0x56,
                                0x84, 0x12, 0x01, 0x00,
                                0x10,
                            ]);
                            Run(5_000_000);
                            machine.QueueInfraredFrame([0x23, 0x51]);
                            Run(5_000_000);
                            if (infraredFrames.Any(frame =>
                                frame.Length >= 7 &&
                                frame[0] == 0x22 &&
                                frame[2] == 0x92 &&
                                frame[4] == 0x81))
                            {
                                machine.QueueInfraredFrame(
                                [
                                    0x23, 0x78,
                                    0x04, 0x12,
                                    0x01,
                                    0x80, 0x00, 0x07,
                                    0x10, 0x00, 0x02, 0x00,
                                ]);
                                Run(5_000_000);
                                machine.QueueInfraredFrame(
                                [
                                    0x23, 0x9a,
                                    0x81, 0x00, 0x81, 0x00,
                                ]);
                                Run(5_000_000);
                                if (infraredFrames.Any(frame =>
                                    frame.Length >= 12 &&
                                    frame[2] == 0x12 &&
                                    frame[5] == 0xa0))
                                {
                                    machine.QueueInfraredFrame(
                                    [
                                        0x23, 0xbc,
                                        0x04, 0x12, 0x01,
                                        0x82, 0x00, 0x29,
                                        0x01, 0x00, 0x0f,
                                        0x00, (byte)'I',
                                        0x00, (byte)'.',
                                        0x00, (byte)'v',
                                        0x00, (byte)'c',
                                        0x00, (byte)'f',
                                        0x00, 0x00,
                                        0x42, 0x00, 0x10,
                                        (byte)'T', (byte)'E', (byte)'X',
                                        (byte)'T', (byte)'/', (byte)'X',
                                        (byte)'-', (byte)'V', (byte)'C',
                                        (byte)'A', (byte)'R', (byte)'D',
                                        0x00,
                                        0x49, 0x00, 0x07,
                                        (byte)'I', (byte)'R',
                                        (byte)'D', (byte)'A',
                                    ]);
                                    Run(10_000_000);
                                    if (infraredFrames.Any(frame =>
                                        frame.Length >= 20 &&
                                        frame[2] == 0x00 &&
                                        frame[4] == 0x84 &&
                                        frame[5] == 0x06))
                                    {
                                        machine.QueueInfraredFrame(
                                        [
                                            0x23, 0xde,
                                            0x01, 0x00,
                                            0x84, 0x00,
                                            0x00, 0x01,
                                            0x00, 0x01,
                                            0x03, 0x00, 0x05,
                                            (byte)'C', (byte)'o',
                                            (byte)'d', (byte)'e',
                                            (byte)'x',
                                        ]);
                                        Run(10_000_000);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        }
    }
    else
    {
        Press("select-infrared-row", down);
        Press("open-infrared", center, 6_000_000);
        if (activate)
        {
            captureInfrared = true;
            Press("activate-infrared", center, 30_000_000);
        }
    }
}

Console.WriteLine(
    $"instructions={machine.ExecutedInstructions:n0} " +
    $"cycles={machine.Cycles:n0} frames={machine.FrameVersion:n0}");
Console.WriteLine(
    $"modem-stopped={modem.IsStopped} kind={modem.StopKind} " +
    $"reason={modem.StopReason ?? "--"}");
Console.WriteLine(
    $"frame-sha256=" +
    Convert.ToHexStringLower(SHA256.HashData(machine.Frame.Span)));
MiaInfraredTransferStatus objectStatus =
    machine.GetInfraredTransferStatus();
Console.WriteLine(
    $"object-peer phase={objectStatus.Phase} " +
    $"sent={objectStatus.ObjectsSentToHandset} " +
    $"received={objectStatus.ObjectsReceivedFromHandset} " +
    $"bytes={objectStatus.ObjectBytesTransferred} " +
    $"message={objectStatus.Message}");
if (objectStatus.ReceivedObject is { } receivedObject)
{
    Console.WriteLine(
        $"object-received name={receivedObject.Name} " +
        $"type={receivedObject.MediaType} " +
        $"bytes={receivedObject.Data.Length}");
}
Console.WriteLine("Infrared-related ARM MMIO:");
foreach (var access in accesses)
{
    Console.WriteLine(
        $"  cycle={access.Cycle:n0} pc=0x{access.Pc:x8} " +
        $"{(access.IsWrite ? "write" : "read")} " +
        $"address=0x{access.Address:x8} size={access.Size} " +
        $"value=0x{access.Value:x8}");
}
Console.WriteLine("Infrared-stage AVR/ARM frames:");
foreach (string frame in linkFrames)
{
    Console.WriteLine($"  {frame}");
}
Console.WriteLine("Infrared-stage ARM OSE signals:");
foreach (string signal in oseSignals)
{
    Console.WriteLine($"  {signal}");
}
Console.WriteLine(
    $"Infrared transmitted wire bytes ({infraredBytes.Count:n0}): " +
    Convert.ToHexString(infraredBytes.ToArray()));
Console.WriteLine("Infrared transmitted IrLAP frames:");
foreach (byte[] frame in infraredFrames)
{
    Console.WriteLine($"  {Convert.ToHexString(frame)}");
}
Console.WriteLine(
    "IrLAP state: " +
    Convert.ToHexString(modem.Bus.SnapshotExternal(0x01420ef0, 0x20)));
Console.WriteLine(
    "LLIrDA state: " +
    Convert.ToHexString(modem.Bus.SnapshotExternal(0x014221a8, 0x3c)));
Console.WriteLine(
    "IrLAP globals: " +
    Convert.ToHexString(modem.Bus.SnapshotExternal(0x01400b18, 0x44)));
Console.WriteLine("IrLAP response trace:");
foreach (string entry in irLapTrace)
{
    Console.WriteLine($"  {entry}");
}
if (linkReceiveTrace.Count != 0)
{
    string tracePath = "/private/tmp/mia-infrared-link-receive.txt";
    File.WriteAllLines(tracePath, linkReceiveTrace);
    Console.WriteLine(
        $"Infrared link receive trace: {tracePath} " +
        $"({linkReceiveTrace.Count:n0} instructions)");
}

void Run(long instructionCount)
{
    long end = machine.ExecutedInstructions + instructionCount;
    while (!machine.IsStopped && machine.ExecutedInstructions < end)
    {
        machine.RunWorkItems(
            (int)Math.Min(262_144, end - machine.ExecutedInstructions));
    }
}

void Press(
    string name,
    Contact contact,
    long postInstructions = 1_500_000)
{
    machine.SetKey(
        contact.ScanMask,
        contact.RowMask,
        contact.SecondaryScanMask,
        pressed: true);
    Run(900_000);
    machine.SetKey(
        contact.ScanMask,
        contact.RowMask,
        contact.SecondaryScanMask,
        pressed: false);
    Run(postInstructions);
    SaveFrame(name);
}

void SaveFrame(string label)
{
    if (frameDirectory is null)
    {
        return;
    }

    string safeLabel = string.Concat(label.Select(character =>
        char.IsLetterOrDigit(character) ? character : '-'));
    string path = Path.Combine(frameDirectory, $"{safeLabel}.ppm");
    using var output = File.Create(path);
    using var header = new StreamWriter(output, leaveOpen: true);
    header.Write("P6\n101 80\n255\n");
    header.Flush();
    Span<byte> rgb = stackalloc byte[3];
    foreach (byte pixel in machine.Frame.Span)
    {
        rgb[0] = (byte)((pixel >> 5) * 255 / 7);
        rgb[1] = (byte)(((pixel >> 2) & 7) * 255 / 7);
        rgb[2] = (byte)((pixel & 3) * 255 / 3);
        output.Write(rgb);
    }
}

static byte[] SnapshotArmMemory(
    ArmModemBus bus,
    uint address,
    int length)
{
    if (address < ArmModemBus.InternalRamSize - length)
    {
        return bus.SnapshotInternal(address, length);
    }
    if (address >= ArmModemBus.ExternalRamBase &&
        address < ArmModemBus.ExternalRamBase +
            ArmModemBus.ExternalRamMirrorSpan - length)
    {
        return bus.SnapshotExternal(address, length);
    }
    return [];
}

readonly record struct Contact(
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask = null);

readonly record struct LinkFrame(byte Destination, byte Source, byte[] Payload);

sealed class NativeLinkFrameDecoder
{
    readonly List<byte> _payload = [];
    int _state;
    int _length;
    byte _destination;
    byte _source;

    public LinkFrame? Push(byte value)
    {
        switch (_state)
        {
            case 0: _state = value == 0xab ? 1 : 0; break;
            case 1: _state = value == 0xba ? 2 : value == 0xab ? 1 : 0; break;
            case 2: _destination = value; _state = 3; break;
            case 3: _length = value; _state = 4; break;
            case 4:
                _length |= value << 8;
                _state = _length <= 4096 ? 5 : 0;
                break;
            case 5:
                _source = value;
                _payload.Clear();
                if (_length == 0)
                {
                    return Complete();
                }
                _state = 6;
                break;
            case 6:
                _payload.Add(value);
                if (_payload.Count == _length)
                {
                    return Complete();
                }
                break;
        }
        return null;
    }

    LinkFrame Complete()
    {
        var frame = new LinkFrame(_destination, _source, _payload.ToArray());
        _state = 0;
        _length = 0;
        _payload.Clear();
        return frame;
    }
}

sealed class AsicLinkFrameDecoder
{
    readonly List<byte> _payload = [];
    int _state;
    int _length;
    byte _destination;
    byte _source;

    public LinkFrame? Push(byte value)
    {
        switch (_state)
        {
            case 0:
                if ((value & 0x80) != 0)
                {
                    _destination = (byte)(value & 0x7f);
                    _state = 1;
                }
                break;
            case 1: _length = value; _state = 2; break;
            case 2:
                _length |= value << 8;
                _state = _length <= 4096 ? 3 : 0;
                break;
            case 3:
                _source = value;
                _payload.Clear();
                if (_length == 0)
                {
                    return Complete();
                }
                _state = 4;
                break;
            case 4:
                _payload.Add(value);
                if (_payload.Count == _length)
                {
                    return Complete();
                }
                break;
        }
        return null;
    }

    LinkFrame Complete()
    {
        var frame = new LinkFrame(_destination, _source, _payload.ToArray());
        _state = 0;
        _length = 0;
        _payload.Clear();
        return frame;
    }
}
