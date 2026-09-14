// SPDX-License-Identifier: BSD-3-Clause
// Adapted from the Noks C# SIM model; its protocol/filesystem design was
// informed by swSIM.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Mia.Emulator.Sim;

internal sealed class SwSimCard
{
    private static readonly SwSimCardFileKey RootKey = new(0, 0x3F00);
    public const string DefaultImsi = "001010000000001";
    private const int SmsStorageRecordLength = 176;
    // READ/UPDATE RECORD uses a one-byte, one-based record number; record 0 is invalid.
    private const int DefaultSmsStorageRecordCount = byte.MaxValue;
    private static readonly HashSet<ushort> VolatileGsmFileIds =
    [
        0x6F20, // EF_Kc
        0x6F30, // EF_PLMNsel
        0x6F31, // EF_HPLMN
        0x6F37, // EF_ACMmax
        0x6F39, // EF_ACM
        0x6F74, // EF_BCCH
        0x6F78, // EF_ACC
        0x6F7B, // EF_FPLMN
        0x6F7E, // EF_LOCI
        0x6FAD, // EF_AD
    ];
    // GSM 11.11 / 3GPP TS 51.011 EF-SMSP, record length 44 (Y=16).
    // Only the service-centre address is present; the compatibility network's
    // logical SMSC is national number 12345. The phone supplies TP destination,
    // PID, DCS, and validity period in each submitted message.
    private const string DefaultSmsParametersRecord =
        "54363869FFFFFFFFFFFFFFFFFFFFFFFF" +
        "FD" +
        "FFFFFFFFFFFFFFFFFFFFFFFF" +
        "04812143F5FFFFFFFFFFFFFF" +
        "FFFFFF";
    private static readonly byte[] DefaultServiceTable = BuildSimServiceTable(
        14,
        1, 2, 4, 6, 7,
        9, 10, 11, 12,
        13, 14, 15, 16,
        17, 18, 19,
        26,
        30,
        35, 38, 56);

    private static readonly byte[] DefaultAtr = [0x3B, 0x00];

    private readonly List<byte> tx = new(32);
    private readonly Dictionary<SwSimCardFileKey, SwSimCardFile> files = [];
    private readonly Dictionary<SwSimCardFileKey, byte[]> persistenceBaseline = [];
    private readonly byte[] imsiEf;
    private readonly byte[] operatorNameEf;
    private readonly byte[] serviceProviderNameEf;
    private readonly byte[] answerToReset;
    private byte[] pendingResponse = [];
    private SimCardResponse? pendingOutput;
    private SwSimCardTransportState transportState;
    private int expectedTxLength;
    private byte pendingIns;
    private SwSimCardFileKey currentDirectory;
    private SwSimCardFileKey selectedFile;

    public string Imsi { get; }

    public long PersistenceVersion { get; private set; }

    public event Action<byte[]>? CommandReceived;

    public event Action<long>? PersistenceChanged;

    public SwSimCard(
        string? imsi = null,
        byte[]? answerToReset = null,
        string? serviceProviderName = null)
    {
        this.answerToReset = (answerToReset ?? DefaultAtr).ToArray();
        Imsi = imsi ?? DefaultImsi;
        imsiEf = EncodeImsi(Imsi);
        operatorNameEf = EncodeAlphaIdentifier(serviceProviderName, 20);
        serviceProviderNameEf = EncodeServiceProviderName(serviceProviderName);
        BuildDefaultFileSystem();
        CapturePersistenceBaseline();
        Reset();
    }

    public ReadOnlySpan<byte> AnswerToReset()
    {
        Reset();
        return answerToReset;
    }

    public void Reset()
    {
        tx.Clear();
        pendingResponse = [];
        pendingOutput = null;
        transportState = SwSimCardTransportState.AwaitingInitialByte;
        expectedTxLength = 0;
        pendingIns = 0;
        currentDirectory = RootKey;
        selectedFile = RootKey;
    }

    public SimCardResponse? Transmit(byte value)
    {
        pendingOutput = null;

        if (transportState == SwSimCardTransportState.AwaitingInitialByte)
        {
            return BeginTransaction(value);
        }

        tx.Add(value);

        switch (transportState)
        {
            case SwSimCardTransportState.Pps:
                TryCompletePps();
                break;
            case SwSimCardTransportState.TpduHeader:
                TryCompleteTpduHeader();
                break;
            case SwSimCardTransportState.CommandData:
                TryCompleteCommandData();
                break;
        }

        return pendingOutput;
    }

    SimCardResponse? BeginTransaction(byte value)
    {
        tx.Clear();
        tx.Add(value);
        if (value == 0xFF)
        {
            transportState = SwSimCardTransportState.Pps;
            expectedTxLength = 0;
            return null;
        }

        transportState = SwSimCardTransportState.TpduHeader;
        expectedTxLength = 5;
        TryCompleteTpduHeader();
        return pendingOutput;
    }

    private void TryCompletePps()
    {
        if (tx.Count == 2)
        {
            expectedTxLength = PpsLength(tx[1]);
        }

        if (expectedTxLength == 0 || tx.Count < expectedTxLength)
        {
            return;
        }

        byte[] pps = tx.ToArray();
        tx.Clear();
        transportState = SwSimCardTransportState.AwaitingInitialByte;
        expectedTxLength = 0;

        if (IsSupportedPps(pps))
        {
            SetResponse(pps, true);
            return;
        }
    }

    private void TryCompleteTpduHeader()
    {
        if (tx.Count < expectedTxLength)
        {
            return;
        }

        pendingIns = tx[1];
        SwSimCardApduResult result = ProcessCommand(CollectionsMarshal.AsSpan(tx), 0, false);

        if (result.ProcedureLength >= 0)
        {
            SetResponse([pendingIns], false);
            transportState = SwSimCardTransportState.CommandData;
            expectedTxLength = 5 + result.ProcedureLength;
            return;
        }

        tx.Clear();
        transportState = SwSimCardTransportState.AwaitingInitialByte;
        expectedTxLength = 0;
        CompleteCommand(pendingIns, result);
    }

    private void TryCompleteCommandData()
    {
        if (tx.Count < expectedTxLength)
        {
            return;
        }

        byte ins = pendingIns;
        SwSimCardApduResult result = ProcessCommand(CollectionsMarshal.AsSpan(tx), 1, true);
        tx.Clear();
        transportState = SwSimCardTransportState.AwaitingInitialByte;
        expectedTxLength = 0;
        pendingIns = 0;
        CompleteCommand(ins, result);
    }

    private SwSimCardApduResult ProcessCommand(ReadOnlySpan<byte> apdu, int procedureCount, bool commandComplete)
    {
        byte cla = apdu[0];
        byte ins = apdu[1];
        byte p1 = apdu[2];
        byte p2 = apdu[3];
        byte p3 = apdu[4];
        ReadOnlySpan<byte> data = apdu[5..];
        var command = new SwSimCardCommand(
            p1,
            p2,
            p3,
            data,
            procedureCount);

        if (commandComplete || procedureCount == 0 && !CommandNeedsData(cla, ins))
        {
            CommandReceived?.Invoke(apdu.ToArray());
        }

        if (procedureCount == 0 && ins != 0xC0)
        {
            pendingResponse = [];
        }

        if (cla != 0xA0)
        {
            return SwSimCardApduResult.Status(0x6E, 0x00);
        }

        return ins switch
        {
            0xA4 => SelectFile(command),
            0xC0 => GetResponse(command),
            0xF2 => Status(command),
            0xB0 => ReadBinary(command),
            0xB2 => ReadRecord(command),
            0xD6 => UpdateBinary(command),
            0xDC => UpdateRecord(command),
            0x20 or 0x24 or 0x26 or 0x28 or 0x2C or 0x88 =>
                SwSimCardApduResult.Status(0x98, 0x04),
            _ => SwSimCardApduResult.Status(0x6D, 0x00),
        };
    }

    private static bool CommandNeedsData(byte cla, byte ins)
    {
        return cla == 0xA0 && ins switch
        {
            0xA4 or 0xD6 or 0xDC => true,
            _ => false,
        };
    }

    private void CompleteCommand(byte ins, SwSimCardApduResult result)
    {
        if (result.Data.Length == 0)
        {
            QueueStatus(result.Sw1, result.Sw2);
            return;
        }

        QueueDataResponse(ins, result.Data, result.Sw1, result.Sw2);
    }

    private SwSimCardApduResult SelectFile(SwSimCardCommand command)
    {
        SwSimCardApduResult? validation = ValidateSelectCommand(command);
        if (validation is not null)
        {
            return validation.Value;
        }

        if (command.Data.Length != 2)
        {
            return SwSimCardApduResult.Status(0x67, 0x02);
        }

        ushort fid = BinaryPrimitives.ReadUInt16BigEndian(command.Data);

        if (!TryResolveFile(fid, out SwSimCardFileKey key, out SwSimCardFile file))
        {
            return SwSimCardApduResult.Status(0x94, 0x04);
        }

        selectedFile = key;
        currentDirectory = file.Kind == SwSimCardFileKind.Directory ? key : DirectoryKey(file.Parent);

        pendingResponse = BuildSelectResponse(file);
        return SwSimCardApduResult.Status(0x9F, (byte)pendingResponse.Length);
    }

    private static SwSimCardApduResult? ValidateSelectCommand(
        SwSimCardCommand command)
    {
        if (command.P1 != 0 || command.P2 != 0 || command.P3 != 2)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }
        if (command.ProcedureCount == 0)
        {
            return command.Data.Length == 0
                ? SwSimCardApduResult.Procedure(2)
                : SwSimCardApduResult.Status(0x6F, 0x00);
        }
        return null;
    }

    private bool TryResolveFile(ushort fid, out SwSimCardFileKey key, out SwSimCardFile file)
    {
        if (fid == RootKey.Id)
        {
            key = RootKey;
            return TryGetFile(key, out file);
        }

        key = new SwSimCardFileKey(currentDirectory.Id, fid);

        if (TryGetFile(key, out file))
        {
            return true;
        }

        key = new SwSimCardFileKey(RootKey.Id, fid);

        if (TryGetFile(key, out file) && file.Kind == SwSimCardFileKind.Directory)
        {
            return true;
        }

        key = default;
        file = null!;
        return false;
    }

    private bool TryGetFile(SwSimCardFileKey key, out SwSimCardFile file)
    {
        if (files.TryGetValue(key, out SwSimCardFile? found) && found is not null)
        {
            file = found;
            return true;
        }

        file = null!;
        return false;
    }

    private static SwSimCardFileKey DirectoryKey(ushort id)
    {
        return id == RootKey.Id ? RootKey : new SwSimCardFileKey(RootKey.Id, id);
    }

    private SwSimCardApduResult GetResponse(SwSimCardCommand command)
    {
        if (command.P1 != 0 || command.P2 != 0 || command.Data.Length != 0)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }

        int count = command.P3 == 0 ? 256 : command.P3;

        if (count > pendingResponse.Length)
        {
            return SwSimCardApduResult.Status(0x6F, 0x00);
        }

        return SwSimCardApduResult.Response(pendingResponse.AsSpan(0, count).ToArray(), 0x90, 0x00);
    }

    private SwSimCardApduResult Status(SwSimCardCommand command)
    {
        if (command.P1 != 0 || command.P2 != 0)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }

        SwSimCardFile file = files[currentDirectory];
        byte[] response = BuildSelectResponse(file);
        int count = command.P3 == 0 ? 256 : command.P3;
        byte[] data = new byte[count];
        response.AsSpan(0, Math.Min(response.Length, count)).CopyTo(data);
        return SwSimCardApduResult.Response(data, 0x90, 0x00);
    }

    private SwSimCardApduResult ReadBinary(SwSimCardCommand command)
    {
        if (command.Data.Length != 0)
        {
            return SwSimCardApduResult.Status(0x6F, 0x00);
        }

        SwSimCardFile file = files[selectedFile];

        if (file.Kind != SwSimCardFileKind.Transparent)
        {
            return SwSimCardApduResult.Status(0x94, 0x08);
        }

        int count = command.P3 == 0 ? 256 : command.P3;
        int offset = (command.P1 << 8) | command.P2;
        return ReadBinaryRange(file, offset, count);
    }

    private static SwSimCardApduResult ReadBinaryRange(
        SwSimCardFile file,
        int offset,
        int count)
    {
        if (count > file.Data.Length)
        {
            return SwSimCardApduResult.Status(0x67, 0x00);
        }

        if (offset + count > file.Data.Length)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }

        return SwSimCardApduResult.Response(file.Data.AsSpan(offset, count).ToArray(), 0x90, 0x00);
    }

    private SwSimCardApduResult ReadRecord(SwSimCardCommand command)
    {
        if (command.Data.Length != 0)
        {
            return SwSimCardApduResult.Status(0x6F, 0x00);
        }

        SwSimCardFile file = files[selectedFile];

        if (!IsReadableRecord(file, command.P1))
        {
            return SwSimCardApduResult.Status(0x94, 0x08);
        }

        if ((command.P2 & 0x07) != 0x04)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }

        int count = command.P3 == 0 ? file.RecordLength : command.P3;
        int offset = (command.P1 - 1) * file.RecordLength;
        return ReadRecordRange(file, offset, count);
    }

    private static bool IsReadableRecord(SwSimCardFile file, byte recordNumber) =>
        file.Kind is SwSimCardFileKind.LinearFixed or SwSimCardFileKind.Cyclic &&
        file.RecordLength != 0 &&
        recordNumber != 0;

    private static SwSimCardApduResult ReadRecordRange(
        SwSimCardFile file,
        int offset,
        int count)
    {
        if (count > file.RecordLength)
        {
            return SwSimCardApduResult.Status(0x67, 0x00);
        }

        if (offset >= file.Data.Length)
        {
            return SwSimCardApduResult.Status(0x94, 0x02);
        }

        return SwSimCardApduResult.Response(file.Data.AsSpan(offset, count).ToArray(), 0x90, 0x00);
    }

    private SwSimCardApduResult UpdateBinary(SwSimCardCommand command)
    {
        if ((command.P1 & 0x80) != 0)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }

        int count = command.P3 == 0 ? 256 : command.P3;
        SwSimCardApduResult? validation = ValidateCommandData(command, count);
        if (validation is not null)
        {
            return validation.Value;
        }

        SwSimCardFile file = files[selectedFile];

        if (file.Kind != SwSimCardFileKind.Transparent)
        {
            return SwSimCardApduResult.Status(0x94, 0x08);
        }

        int offset = (command.P1 << 8) | command.P2;
        return UpdateBinaryRange(file, offset, command.Data);
    }

    private SwSimCardApduResult UpdateBinaryRange(
        SwSimCardFile file,
        int offset,
        ReadOnlySpan<byte> data)
    {
        if (offset + data.Length > file.Data.Length)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }

        data.CopyTo(file.Data.AsSpan(offset, data.Length));
        NotifyPersistenceChanged();
        return SwSimCardApduResult.Status(0x90, 0x00);
    }

    private SwSimCardApduResult UpdateRecord(SwSimCardCommand command)
    {
        int count = command.P3 == 0 ? 256 : command.P3;
        SwSimCardApduResult? validation = ValidateCommandData(command, count);
        if (validation is not null)
        {
            return validation.Value;
        }

        SwSimCardFile file = files[selectedFile];

        SwSimCardApduResult? recordValidation = ValidateRecordWrite(
            file,
            command.P1,
            command.P2,
            count);
        if (recordValidation is not null)
        {
            return recordValidation.Value;
        }

        int offset = (command.P1 - 1) * file.RecordLength;
        if (offset >= file.Data.Length)
        {
            return SwSimCardApduResult.Status(0x94, 0x02);
        }

        command.Data.CopyTo(file.Data.AsSpan(offset, count));
        NotifyPersistenceChanged();
        return SwSimCardApduResult.Status(0x90, 0x00);
    }

    private static SwSimCardApduResult? ValidateCommandData(
        SwSimCardCommand command,
        int count)
    {
        if (command.ProcedureCount == 0)
        {
            return command.Data.Length == 0
                ? SwSimCardApduResult.Procedure(count)
                : SwSimCardApduResult.Status(0x6F, 0x00);
        }
        if (command.Data.Length != count)
        {
            return SwSimCardApduResult.Status(0x67, 0x00);
        }
        return null;
    }

    private static SwSimCardApduResult? ValidateRecordWrite(
        SwSimCardFile file,
        byte recordNumber,
        byte mode,
        int count)
    {
        if (!IsReadableRecord(file, recordNumber))
        {
            return SwSimCardApduResult.Status(0x94, 0x08);
        }
        if ((mode & 0x07) != 0x04)
        {
            return SwSimCardApduResult.Status(0x6B, 0x00);
        }
        if (count != file.RecordLength)
        {
            return SwSimCardApduResult.Status(0x67, 0x00);
        }
        return null;
    }

    public void ApplyOverlay(IEnumerable<SimFileOverlay> overlays)
    {
        foreach (SimFileOverlay overlay in overlays)
        {
            SwSimCardFileKey key = new(overlay.Parent, overlay.Id);
            if (!files.TryGetValue(key, out SwSimCardFile? file) ||
                file.Kind == SwSimCardFileKind.Directory ||
                overlay.Data.Length != file.Data.Length ||
                !ShouldPersistFile(key))
            {
                continue;
            }

            overlay.Data.AsSpan().CopyTo(file.Data);
        }

        NotifyPersistenceChanged();
    }

    private void NotifyPersistenceChanged()
    {
        PersistenceVersion++;
        PersistenceChanged?.Invoke(PersistenceVersion);
    }

    public SimFileOverlay[] CreateOverlay()
    {
        List<SimFileOverlay> overlays = [];

        foreach ((SwSimCardFileKey key, SwSimCardFile file) in files)
        {
            if (file.Kind == SwSimCardFileKind.Directory ||
                !persistenceBaseline.TryGetValue(key, out byte[]? baseline) ||
                !ShouldPersistFile(key) ||
                file.Data.AsSpan().SequenceEqual(baseline))
            {
                continue;
            }

            overlays.Add(new SimFileOverlay(key.Parent, key.Id, file.Data.ToArray()));
        }

        return overlays.ToArray();
    }

    private void CapturePersistenceBaseline()
    {
        persistenceBaseline.Clear();

        foreach ((SwSimCardFileKey key, SwSimCardFile file) in files)
        {
            if (file.Kind != SwSimCardFileKind.Directory)
            {
                persistenceBaseline[key] = file.Data.ToArray();
            }
        }
    }

    private static bool ShouldPersistFile(SwSimCardFileKey key) =>
        !IsGsmApplicationDirectory(key.Parent) || !VolatileGsmFileIds.Contains(key.Id);

    private static bool IsGsmApplicationDirectory(ushort parent) =>
        parent is 0x7F20 or 0x7F21 or 0x7F40;

    private void QueueDataResponse(byte procedure, ReadOnlySpan<byte> data, byte sw1, byte sw2)
    {
        byte[] response = new byte[1 + data.Length + 2];
        response[0] = procedure;
        data.CopyTo(response.AsSpan(1));
        response[^2] = sw1;
        response[^1] = sw2;
        SetResponse(response, true);
    }

    private void QueueStatus(byte sw1, byte sw2)
    {
        SetResponse([sw1, sw2], true);
    }

    private void SetResponse(ReadOnlySpan<byte> data, bool complete)
    {
        pendingOutput = new SimCardResponse(data.ToArray(), complete);
    }

    private byte[] BuildSelectResponse(SwSimCardFile file)
    {
        return file.Kind == SwSimCardFileKind.Directory
            ? BuildDirectorySelectResponse(file)
            : BuildElementaryFileSelectResponse(file);
    }

    private byte[] BuildDirectorySelectResponse(SwSimCardFile file)
    {
        byte[] response = new byte[23];
        response[2] = 0xFF;
        response[3] = 0xFF;
        response[4] = (byte)(file.Id >> 8);
        response[5] = (byte)file.Id;
        response[6] = file.Id == 0x3F00 ? (byte)0x01 : (byte)0x02;
        response[12] = 0x0A;
        response[13] = 0xB2;
        response[14] = CountChildren(file.Id, SwSimCardFileKind.Directory);
        response[15] = CountChildren(file.Id, SwSimCardFileKind.Transparent, SwSimCardFileKind.LinearFixed, SwSimCardFileKind.Cyclic);
        response[16] = 0x04;
        response[18] = 0x83;
        response[19] = 0x8A;
        response[20] = 0x83;
        response[21] = 0x8A;
        return response;
    }

    private static byte[] BuildElementaryFileSelectResponse(SwSimCardFile file)
    {
        byte[] response = new byte[15];
        int size = file.Data.Length;
        response[2] = (byte)(size >> 8);
        response[3] = (byte)size;
        response[4] = (byte)(file.Id >> 8);
        response[5] = (byte)file.Id;
        response[6] = 0x04;
        response[11] = 0x01;
        response[12] = 0x02;
        response[13] = file.Kind switch
        {
            SwSimCardFileKind.LinearFixed => 0x01,
            SwSimCardFileKind.Cyclic => 0x03,
            _ => 0x00,
        };
        response[14] = file.Kind == SwSimCardFileKind.Transparent ? (byte)0x00 : (byte)Math.Min(file.RecordLength, 0xFF);
        return response;
    }

    private byte CountChildren(ushort parent, params SwSimCardFileKind[] kinds)
    {
        int count = files.Values.Count(file => file.Parent == parent && kinds.Contains(file.Kind));
        return (byte)Math.Min(count, 0xFF);
    }

    private void BuildDefaultFileSystem()
    {
        AddDirectory(0x3F00, 0);
        AddDirectory(0x7F10, 0x3F00);
        AddDirectory(0x7F20, 0x3F00);
        AddDirectory(0x7F21, 0x3F00);
        AddDirectory(0x7F40, 0x3F00);
        AddTransparent(0x2FE2, 0x3F00, "989999900000000000F1");
        AddGsmApplicationFiles(0x7F20);
        AddGsmApplicationFiles(0x7F21);
        AddGsmApplicationFiles(0x7F40);
        AddPcsCompatibilityFiles(0x7F40);
        AddTelecomFiles(0x7F10);
    }

    private void AddGsmApplicationFiles(ushort parent)
    {
        // EF-LP: prefer English, retain Polish as the next supported language.
        AddTransparent(0x6F05, parent, "010EFFFF");
        AddTransparent(0x6F07, parent, imsiEf);
        AddTransparent(0x6F14, parent, operatorNameEf);
        AddTransparent(0x6F20, parent, "FFFFFFFFFFFFFFFF07");
        AddTransparent(0x6F30, parent, "FFFFFF");
        AddTransparent(0x6F31, parent, "05");
        AddTransparent(0x6F37, parent, "000000");
        // FDN (service 3) is intentionally not advertised. The current SIM
        // model does not yet track EF invalidation/rehabilitation state, which
        // the firmware uses to decide whether FDN leaves the ME restricted.
        AddTransparent(0x6F38, parent, DefaultServiceTable);
        AddCyclic(0x6F39, parent, 3, "000000", "000000", "000000");
        AddTransparent(0x6F3E, parent, "FFFFFFFF");
        AddTransparent(0x6F3F, parent, "FFFFFFFF");
        AddTransparent(0x6F41, parent, "FFFFFF0000");
        AddTransparent(0x6F45, parent, "FFFFFFFFFFFFFFFFFFFF");
        AddTransparent(0x6F46, parent, serviceProviderNameEf);
        AddTransparent(0x6F48, parent, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
        AddTransparent(0x6F74, parent, "00000000000000000000000000000000");
        AddTransparent(0x6F78, parent, "0080");
        AddTransparent(0x6F7B, parent, "FFFFFFFFFFFFFFFFFFFFFFFF");
        AddTransparent(0x6F7E, parent, "FFFFFFFFFFFFFFFFFFFF00");
        AddTransparent(0x6FAD, parent, "00FFFF");
        AddTransparent(0x6FAE, parent, "02");
        AddTransparent(0x6FB5, parent, "0000");
        AddTransparent(0x6FB6, parent, "00");
        AddLinearFixed(0x6FB7, parent, 16, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00");
    }

    private void AddPcsCompatibilityFiles(ushort parent)
    {
        AddTransparentFilled(0x6F13, parent, 1);
        AddTransparentFilled(0x6F91, parent, 1);
        AddTransparentFilled(0x6F92, parent, 1);
        AddTransparentFilled(0x6F93, parent, 1);
        AddTransparentFilled(0x6F95, parent, 0x1D);
        AddTransparentFilled(0x6F96, parent, 0x1D);
        AddTransparentFilled(0x6F98, parent, 0x16);
        AddTransparentFilled(0x6F9F, parent, 1);
    }

    private void AddTelecomFiles(ushort parent)
    {
        AddLinearFixed(0x6F3A, parent, 30, 2);
        AddLinearFixed(0x6F3B, parent, 30, 2);
        AddSmsStorageFile(0x6F3C, parent, DefaultSmsStorageRecordCount);
        AddLinearFixed(0x6F3D, parent, 14, 3);
        AddLinearFixed(0x6F40, parent, 30, 2);
        AddLinearFixed(
            0x6F42,
            parent,
            44,
            DefaultSmsParametersRecord,
            DefaultSmsParametersRecord);
        AddTransparent(0x6F43, parent, "04FF");
        AddCyclic(0x6F44, parent, 30, 3);
        AddLinearFixed(0x6F47, parent, 30, 2);
        AddLinearFixed(0x6F49, parent, 30, 2);
        AddLinearFixed(0x6F4A, parent, 13, 2);
        AddLinearFixed(0x6F4B, parent, 13, 2);
        AddLinearFixed(0x6F4C, parent, 13, 2);
    }

    private void AddDirectory(ushort id, ushort parent)
    {
        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.Directory, 0, 0, []));
    }

    private void AddTransparent(ushort id, ushort parent, string hex)
    {
        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.Transparent, 0, 0, Convert.FromHexString(hex)));
    }

    private void AddTransparent(ushort id, ushort parent, ReadOnlySpan<byte> data)
    {
        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.Transparent, 0, 0, data.ToArray()));
    }

    private void AddTransparentFilled(ushort id, ushort parent, int length)
    {
        byte[] data = new byte[length];
        Array.Fill(data, (byte)0xFF);
        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.Transparent, 0, 0, data));
    }

    private void AddLinearFixed(ushort id, ushort parent, int recordLength, int records)
    {
        byte[] data = new byte[recordLength * records];
        Array.Fill(data, (byte)0xFF);
        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.LinearFixed, 1, recordLength, data));
    }

    private void AddSmsStorageFile(ushort id, ushort parent, int records)
    {
        const int recordLength = SmsStorageRecordLength;

        if (records is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(records), "SIM record numbers are one byte and record 0 is invalid.");
        }

        byte[] data = new byte[recordLength * records];
        Array.Fill(data, (byte)0xFF);

        for (int record = 0; record < records; record++)
        {
            data[record * recordLength] = 0x00;
        }

        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.LinearFixed, 1, recordLength, data));
    }

    private void AddLinearFixed(ushort id, ushort parent, int recordLength, params string[] records)
    {
        byte[] data = new byte[recordLength * records.Length];

        for (int i = 0; i < records.Length; i++)
        {
            byte[] record = Convert.FromHexString(records[i]);

            if (record.Length != recordLength)
            {
                throw new InvalidOperationException("SIM record length mismatch.");
            }

            record.CopyTo(data.AsSpan(i * recordLength));
        }

        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.LinearFixed, 1, recordLength, data));
    }

    private void AddCyclic(ushort id, ushort parent, int recordLength, int records)
    {
        byte[] data = new byte[recordLength * records];
        Array.Fill(data, (byte)0xFF);
        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.Cyclic, 3, recordLength, data));
    }

    private void AddCyclic(ushort id, ushort parent, int recordLength, params string[] records)
    {
        byte[] data = new byte[recordLength * records.Length];

        for (int i = 0; i < records.Length; i++)
        {
            byte[] record = Convert.FromHexString(records[i]);

            if (record.Length != recordLength)
            {
                throw new InvalidOperationException("SIM record length mismatch.");
            }

            record.CopyTo(data.AsSpan(i * recordLength));
        }

        files.Add(new SwSimCardFileKey(parent, id), new SwSimCardFile(id, parent, SwSimCardFileKind.Cyclic, 3, recordLength, data));
    }

    private static int PpsLength(byte pps0)
    {
        return 3
            + ((pps0 & 0x10) != 0 ? 1 : 0)
            + ((pps0 & 0x20) != 0 ? 1 : 0)
            + ((pps0 & 0x40) != 0 ? 1 : 0);
    }

    private static bool IsSupportedPps(ReadOnlySpan<byte> pps)
    {
        if (pps.Length < 3 || pps[0] != 0xFF || pps.Length != PpsLength(pps[1]))
        {
            return false;
        }

        if ((pps[1] & 0x8F) != 0)
        {
            return false;
        }

        byte checksum = 0;

        foreach (byte value in pps)
        {
            checksum ^= value;
        }

        return checksum == 0;
    }

    private static byte[] EncodeImsi(string imsi)
    {
        if (imsi.Length != 15 || imsi.Any(ch => ch < '0' || ch > '9'))
        {
            throw new ArgumentException("SIM IMSI must be 15 decimal digits.", nameof(imsi));
        }

        byte[] data = new byte[9];
        data[0] = 0x08;
        data[1] = (byte)((Digit(imsi[0]) << 4) | 0x09);
        int digit = 1;

        for (int i = 2; i < data.Length; i++)
        {
            int lo = digit < imsi.Length ? Digit(imsi[digit++]) : 0xF;
            int hi = digit < imsi.Length ? Digit(imsi[digit++]) : 0xF;
            data[i] = (byte)(lo | (hi << 4));
        }

        return data;
    }

    private static int Digit(char value) => value - '0';

    private static byte[] EncodeServiceProviderName(string? serviceProviderName)
    {
        byte[] data = EncodeAlphaIdentifier(serviceProviderName, 17, destinationOffset: 1);
        // EFSPN byte 1 bit 1 = 0: the registered PLMN name does not also have
        // to be displayed. The configured SPN is therefore the idle-screen name.
        data[0] = 0x00;

        return data;
    }

    private static byte[] EncodeAlphaIdentifier(string? value, int length, int destinationOffset = 0)
    {
        string name = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
        byte[] data = new byte[length];
        Array.Fill(data, (byte)0xFF);

        int textLength = Math.Min(name.Length, data.Length - destinationOffset);
        for (int index = 0; index < textLength; index++)
        {
            char character = name[index];
            data[index + destinationOffset] = character is >= ' ' and <= '~'
                ? (byte)(character & 0x7F)
                : (byte)' ';
        }

        return data;
    }

    private static byte[] BuildSimServiceTable(int length, params int[] activeServices)
    {
        if (length < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "EFSST must contain at least two bytes.");
        }

        byte[] table = new byte[length];

        foreach (int service in activeServices)
        {
            if (service < 1 || service > length * 4)
            {
                throw new ArgumentOutOfRangeException(nameof(activeServices), "SIM service is outside the EFSST length.");
            }

            int index = (service - 1) / 4;
            int shift = ((service - 1) % 4) * 2;
            table[index] |= (byte)(0b11 << shift);
        }

        return table;
    }
}
