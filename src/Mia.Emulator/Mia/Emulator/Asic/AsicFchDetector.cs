// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible frequency-correction detector. PH starts one attempt with
/// the proven 5, 6, 0x7f sequence, waits through its own fixed state-machine
/// delay, then writes command 4 before reading seven result bytes. Results are
/// derived only from an explicit RF source; no signal clears stale readback.
/// </summary>
internal sealed class AsicFchDetector
{
    public const int CommandAddress = 0x0a10;
    public const int ParameterAddress = 0x0a11;
    public const int FirstResultAddress = 0x0a12;
    public const int LastResultAddress = 0x0a18;
    public const int ResultLength =
        LastResultAddress - FirstResultAddress + 1;
    public const byte ArmCommand = 0x05;
    public const byte StartCommand = 0x06;
    public const byte ReadResultCommand = 0x04;
    public const byte StartParameter = 0x7f;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicRfFrontend? _rfFrontend;
    readonly IAsicFchSource _source;
    byte _command;
    byte _parameter;
    bool _armed;
    bool _awaitingParameter;
    AsicFchResult? _pendingResult;

    public AsicFchDetector(
        Cpu cpu,
        MiaSystemClock clock,
        AsicRfFrontend? rfFrontend = null,
        IAsicFchSource? source = null)
    {
        _cpu = cpu;
        _clock = clock;
        _rfFrontend = rfFrontend;
        _source = source ?? AsicNoSignalFchSource.Instance;

        cpu.WriteHooks[CommandAddress] = WriteCommand;
        cpu.WriteHooks[ParameterAddress] = WriteParameter;
    }

    public long StartedCount { get; private set; }

    public long CompletedCount { get; private set; }

    public long SuccessfulCount { get; private set; }

    bool WriteCommand(byte value, byte _, int __, byte mask)
    {
        _command = Merge(_command, value, mask);
        switch (_command)
        {
            case ArmCommand:
                _armed = true;
                _awaitingParameter = false;
                break;
            case StartCommand when _armed && _pendingResult is null:
                _awaitingParameter = true;
                break;
            case ReadResultCommand when _pendingResult is not null:
                CompleteAttempt();
                _armed = false;
                _awaitingParameter = false;
                break;
            default:
                _armed = false;
                _awaitingParameter = false;
                break;
        }
        return false;
    }

    bool WriteParameter(byte value, byte _, int __, byte mask)
    {
        _parameter = Merge(_parameter, value, mask);
        if (!_armed || !_awaitingParameter ||
            _command != StartCommand ||
            _parameter != StartParameter ||
            _pendingResult is not null)
        {
            return false;
        }

        var transactions = _rfFrontend?.GetTransactionsSnapshot() ?? [];
        _pendingResult = _source.Detect(new(
            _clock.Cycles,
            transactions));
        _awaitingParameter = false;
        StartedCount++;
        return false;
    }

    void CompleteAttempt()
    {
        var result = _pendingResult ?? throw new InvalidOperationException(
            "FCH completion has no pending attempt.");
        _pendingResult = null;

        var success = result.Success && result.Bytes.Length == ResultLength;
        var destination = _cpu.Data.AsSpan(FirstResultAddress, ResultLength);
        if (success)
        {
            result.Bytes.Span.CopyTo(destination);
            SuccessfulCount++;
        }
        else
        {
            destination.Clear();
        }
        CompletedCount++;
    }

    static byte Merge(byte oldValue, byte value, byte mask) =>
        (byte)((oldValue & ~mask) | (value & mask));
}
