// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible byte-transfer engine used after equalizer completion.
/// The staging and completion protocol is recovered from firmware, while the
/// undocumented selector is deliberately a compatibility convention: selector
/// zero is identity and other values rotate the source forward modulo length.
/// </summary>
internal sealed class AsicTransferController
{
    public const int DescriptorAddressLow = 0x0820;
    public const int DescriptorLengthLow = 0x0822;
    public const int DescriptorControl = 0x0824;
    public const int ControlAddress = 0x0a20;
    public const int SelectorAddress = 0x0a21;
    public const int FirstParameterAddress = 0x0a24;
    public const int LastParameterAddress = 0x0a26;
    public const byte StartControl = 0x01;
    public const byte CompleteMask = 0x08;
    public const byte LinearSourceControl = 0x00;
    public const byte CircularDestinationControl = 0x21;
    public const byte StrideTwoSourceControl = 0x20;
    public const byte LinearDestinationControl = 0x01;
    public const ushort ControlChannelMode = 0x0020;
    public const int NormalBurstHalfLength = 57;
    public const int ControlChannelBlockLength = 456;
    public const ushort Phase2LowerHalfAddress = 0x1887;
    public const ushort Phase2UpperHalfAddress = 0x18c0;
    public const int DataBankBase = 0x100000;
    public const int CompletionCycles = 1;

    const int MaximumStagedDescriptors = 16;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly MiaWorker? _worker;
    readonly List<AsicTransferDescriptor> _stagedDescriptors = [];
    readonly ushort[] _parameters = new ushort[3];
    readonly byte[] _parameterByteCounts = new byte[3];
    AsicTransferDescriptor? _sourceDescriptor;
    AsicTransferDescriptor? _destinationDescriptor;
    AsicTransferControllerPendingTransfer? _pendingTransfer;
    byte _selector;
    byte _status;
    bool _descriptorDataDirty;

    public AsicTransferController(
        Cpu cpu,
        MiaSystemClock clock,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _clock = clock;
        _worker = worker;

        for (var address = DescriptorAddressLow;
             address < DescriptorControl;
             address++)
        {
            InstallDescriptorDataHook(address);
        }
        InstallDescriptorControlHook();

        cpu.ReadHooks[ControlAddress] = _ => Volatile.Read(ref _status);
        cpu.WriteHooks[ControlAddress] = WriteControl;
        cpu.WriteHooks[SelectorAddress] = WriteSelector;
        for (var address = FirstParameterAddress;
             address <= LastParameterAddress;
             address++)
        {
            var parameter = address - FirstParameterAddress;
            cpu.WriteHooks[address] = (value, _, _, _) =>
            {
                WriteParameter(parameter, value);
                return false;
            };
        }
    }

    public long StartedCount { get; private set; }

    public long CompletedCount { get; private set; }

    public long RejectedCount { get; private set; }

    public byte LastSelector { get; private set; }

    public AsicTransferDescriptor? LastSourceDescriptor { get; private set; }

    public AsicTransferDescriptor? LastDestinationDescriptor { get; private set; }

    public ushort LastParameter0 { get; private set; }

    public ushort LastParameter1 { get; private set; }

    public ushort LastParameter2 { get; private set; }

    void InstallDescriptorDataHook(int address)
    {
        var previous = _cpu.WriteHooks[address];
        _cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
        {
            _descriptorDataDirty = true;
            return previous?.Invoke(value, oldValue, hookAddress, mask) ?? false;
        };
    }

    void InstallDescriptorControlHook()
    {
        var previous = _cpu.WriteHooks[DescriptorControl];
        _cpu.WriteHooks[DescriptorControl] =
            (value, oldValue, hookAddress, mask) =>
            {
                StageDescriptor(oldValue, value, mask);

                return previous?.Invoke(value, oldValue, hookAddress, mask) ?? false;
            };
    }

    void StageDescriptor(byte oldValue, byte value, byte mask)
    {
        if (!_descriptorDataDirty)
        {
            return;
        }

        var descriptor = new AsicTransferDescriptor(
            (ushort)(_cpu.Data[DescriptorAddressLow] |
                (_cpu.Data[DescriptorAddressLow + 1] << 8)),
            (ushort)(_cpu.Data[DescriptorLengthLow] |
                (_cpu.Data[DescriptorLengthLow + 1] << 8)),
            Merge(oldValue, value, mask));
        _stagedDescriptors.Add(descriptor);
        TrimStagedDescriptors();
        _descriptorDataDirty = false;
    }

    void TrimStagedDescriptors()
    {
        if (_stagedDescriptors.Count > MaximumStagedDescriptors)
        {
            _stagedDescriptors.RemoveAt(0);
        }
    }

    bool WriteControl(byte value, byte _, int __, byte mask)
    {
        value = Merge(_status, value, mask);
        switch (value)
        {
            case 0:
                ResetTransaction();
                break;
            case StartControl when _pendingTransfer is null:
                StartTransaction();
                break;
            default:
                Volatile.Write(ref _status, value);
                _cpu.Data[ControlAddress] = value;
                break;
        }
        return true;
    }

    bool WriteSelector(byte value, byte _, int __, byte mask)
    {
        _selector = Merge(_selector, value, mask);
        return false;
    }

    void WriteParameter(int parameter, byte value)
    {
        var byteIndex = _parameterByteCounts[parameter]++;
        if ((byteIndex & 1) == 0)
        {
            _parameters[parameter] = value;
        }
        else
        {
            _parameters[parameter] |= (ushort)(value << 8);
        }
    }

    void ResetTransaction()
    {
        Volatile.Write(ref _status, (byte)0);
        _cpu.Data[ControlAddress] = 0;
        _pendingTransfer = null;
        Array.Clear(_parameters);
        Array.Clear(_parameterByteCounts);

        if (_stagedDescriptors.Count >= 2)
        {
            _sourceDescriptor = _stagedDescriptors[^2];
            _destinationDescriptor = _stagedDescriptors[^1];
        }
        else
        {
            _sourceDescriptor = null;
            _destinationDescriptor = null;
        }
        _stagedDescriptors.Clear();
    }

    void StartTransaction()
    {
        LastSourceDescriptor = _sourceDescriptor;
        LastDestinationDescriptor = _destinationDescriptor;
        LastSelector = _selector;
        LastParameter0 = _parameters[0];
        LastParameter1 = _parameters[1];
        LastParameter2 = _parameters[2];
        if (_sourceDescriptor is not { } source ||
            _destinationDescriptor is not { } destination ||
            !TryCreateTransfer(source, destination, out var transfer))
        {
            RejectedCount++;
            Volatile.Write(ref _status, StartControl);
            _cpu.Data[ControlAddress] = StartControl;
            return;
        }

        _pendingTransfer = transfer;
        StartedCount++;
        Volatile.Write(ref _status, StartControl);
        _cpu.Data[ControlAddress] = StartControl;
        if (_worker is null)
        {
            _clock.Schedule(CompleteTransaction, CompletionCycles);
        }
        else
        {
            _clock.Schedule(_worker, CompleteTransaction, CompletionCycles);
        }
    }

    void CompleteTransaction()
    {
        var transfer = _pendingTransfer ?? throw new InvalidOperationException(
            "Transfer completion has no pending transaction.");
        _pendingTransfer = null;

        switch (transfer.Operation)
        {
            case AsicTransferControllerTransferOperation.CircularCopy:
                CompleteCircularCopy(transfer);
                break;
            case AsicTransferControllerTransferOperation.ControlChannelDeinterleave:
                CompleteControlChannelDeinterleave(transfer);
                break;
            default:
                throw new InvalidOperationException(
                    "Transfer completion has no operation.");
        }

        CompletedCount++;
        Volatile.Write(ref _status, CompleteMask);
        _cpu.Data[ControlAddress] = CompleteMask;
    }

    bool IsDataRangeValid(AsicTransferDescriptor descriptor) =>
        (long)DataBankBase + descriptor.Address + descriptor.Length <=
        _cpu.Data.Length;

    bool TryCreateTransfer(
        AsicTransferDescriptor source,
        AsicTransferDescriptor destination,
        out AsicTransferControllerPendingTransfer? transfer)
    {
        if (source.Control == LinearSourceControl &&
            destination.Control == CircularDestinationControl &&
            source.Length != 0 &&
            source.Length == destination.Length &&
            _parameters[0] == 0 &&
            _parameters[2] == 0 &&
            IsDataRangeValid(source) &&
            IsDataRangeValid(destination))
        {
            transfer = new(
                AsicTransferControllerTransferOperation.CircularCopy,
                source,
                destination,
                _selector,
                0,
                0);
            return true;
        }

        if (source.Control == StrideTwoSourceControl &&
            destination.Control == LinearDestinationControl &&
            source.Length is 28 or 29 &&
            source.Length == destination.Length &&
            _parameters[0] == destination.Address &&
            _parameters[1] < ControlChannelBlockLength &&
            _parameters[2] == ControlChannelMode &&
            TryResolveNormalBurstHalf(source.Address, out var halfAddress, out var lane) &&
            IsDataRangeValid(new(
                halfAddress,
                NormalBurstHalfLength,
                LinearSourceControl)) &&
            (long)DataBankBase + destination.Address +
                ControlChannelBlockLength <= _cpu.Data.Length)
        {
            transfer = new(
                AsicTransferControllerTransferOperation.ControlChannelDeinterleave,
                source,
                destination,
                _selector,
                lane,
                _parameters[1]);
            return true;
        }

        transfer = null;
        return false;
    }

    void CompleteCircularCopy(AsicTransferControllerPendingTransfer transfer)
    {
        var length = transfer.Source.Length;
        var sourceAddress = DataBankBase + transfer.Source.Address;
        var destinationAddress = DataBankBase + transfer.Destination.Address;
        var source = _cpu.Data.AsSpan(sourceAddress, length).ToArray();
        var displacement = transfer.Selector % length;
        var destination = _cpu.Data.AsSpan(destinationAddress, length);
        for (var index = 0; index < length; index++)
        {
            destination[index] = source[(index + displacement) % length];
        }
    }

    void CompleteControlChannelDeinterleave(AsicTransferControllerPendingTransfer transfer)
    {
        _ = TryResolveNormalBurstHalf(
            transfer.Source.Address,
            out var halfAddress,
            out _);
        var source = _cpu.Data.AsSpan(
            DataBankBase + halfAddress,
            NormalBurstHalfLength).ToArray();
        var destination = _cpu.Data.AsSpan(
            DataBankBase + transfer.Destination.Address,
            ControlChannelBlockLength);
        var displacement = transfer.Selector % NormalBurstHalfLength;
        for (var index = 0; index < transfer.Source.Length; index++)
        {
            var sourceIndex =
                (transfer.SourceLane + 2 * index + displacement) %
                NormalBurstHalfLength;
            var destinationIndex =
                (transfer.DestinationStart + 64 * index) %
                ControlChannelBlockLength;
            destination[destinationIndex] = source[sourceIndex];
        }
    }

    static bool TryResolveNormalBurstHalf(
        ushort sourceAddress,
        out ushort halfAddress,
        out byte lane)
    {
        if (sourceAddress is >= Phase2LowerHalfAddress and
            <= Phase2LowerHalfAddress + 1)
        {
            halfAddress = Phase2LowerHalfAddress;
            lane = (byte)(sourceAddress - halfAddress);
            return true;
        }
        if (sourceAddress is >= Phase2UpperHalfAddress and
            <= Phase2UpperHalfAddress + 1)
        {
            halfAddress = Phase2UpperHalfAddress;
            lane = (byte)(sourceAddress - halfAddress);
            return true;
        }

        halfAddress = 0;
        lane = 0;
        return false;
    }

    static byte Merge(byte oldValue, byte value, byte mask) =>
        (byte)((oldValue & ~mask) | (value & mask));
}
