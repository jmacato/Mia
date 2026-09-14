// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Power;

/// <summary>
/// Minimal model of the second device on the primary ASIC I2C bus. The native
/// firmware owns all output state. Only recovered latches and read selectors
/// are modeled; opaque writes are acknowledged and reported without inventing
/// a side effect, while unknown reads fail closed.
/// </summary>
internal sealed class MiaSecondaryPortController
{
    public const byte WriteAddress = 0x92;
    public const byte ReadAddress = 0x93;
    public const byte StatusSelector = 0x01;
    public const byte IdentitySelector = 0x0f;
    public const byte Output40Register = 0x40;
    public const byte Output48Register = 0x48;
    public const byte Output80Register = 0x80;

    readonly MiaWorker? _worker;
    byte _selectedRegister;
    byte _register40;
    byte _register48;
    byte _register80;

    public MiaSecondaryPortController(
        MiaSecondaryPortProfile? profile = null,
        MiaWorker? worker = null)
    {
        Profile = profile ?? MiaSecondaryPortProfile.Default;
        if (!IsFirmwareAcceptedIdentity(Profile.Identity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                $"Identity 0x{Profile.Identity:x2} is rejected by the native firmware.");
        }
        _worker = worker;
    }

    public MiaSecondaryPortProfile Profile { get; }

    public MiaSecondaryPortOutputState OutputState =>
        Invoke(CaptureOutputState);

    public long ReadCount { get; private set; }

    public long WriteCount { get; private set; }

    public long OpaqueWriteCount { get; private set; }

    public long UnsupportedReadCount { get; private set; }

    public long RejectedWriteCount { get; private set; }

    public event Action<MiaSecondaryPortOutputState>? OutputStateChanged;

    public event Action<byte, byte>? OpaqueRegisterWritten;

    public static bool Acknowledges(byte address) =>
        (address & 0xfe) == WriteAddress;

    public void WriteTransaction(byte address, byte[] payload) =>
        Invoke(() => WriteTransactionCore(address, payload));

    void WriteTransactionCore(byte address, byte[] payload)
    {
        if (!TrySelectWrite(address, payload, out byte value))
        {
            return;
        }

        bool changed;
        switch (_selectedRegister)
        {
            case Output40Register:
                changed = SetOutput(ref _register40, value);
                break;
            case Output48Register:
                changed = SetOutput(ref _register48, value);
                break;
            case Output80Register:
                changed = SetOutput(ref _register80, value);
                break;
            default:
                OpaqueWriteCount++;
                OpaqueRegisterWritten?.Invoke(_selectedRegister, value);
                WriteCount++;
                return;
        }

        WriteCount++;
        if (changed)
        {
            OutputStateChanged?.Invoke(CaptureOutputState());
        }
    }

    bool TrySelectWrite(byte address, byte[] payload, out byte value)
    {
        value = 0;
        if (address != WriteAddress || payload.Length == 0)
        {
            RejectedWriteCount++;
            return false;
        }

        _selectedRegister = payload[0];
        if (payload.Length == 1)
        {
            return false;
        }
        if (payload.Length != 2)
        {
            RejectedWriteCount++;
            return false;
        }

        value = payload[1];
        return true;
    }

    public byte ReadByte(byte address) => Invoke(() => ReadByteCore(address));

    byte ReadByteCore(byte address)
    {
        if (address != ReadAddress)
        {
            UnsupportedReadCount++;
            throw new InvalidOperationException(
                $"Unsupported secondary-port read address 0x{address:x2}.");
        }

        ReadCount++;
        return _selectedRegister switch
        {
            IdentitySelector => Profile.Identity,
            StatusSelector => Profile.Status,
            _ => ThrowUnsupportedRead(),
        };
    }

    public static bool IsFirmwareAcceptedIdentity(byte value) =>
        value is 0x41 or 0x42 or 0x44 or 0x45 or
            >= 0x61 and <= 0x66 or
            >= 0x91 and <= 0x98 or
            >= 0xa1 and <= 0xa4;

    static bool SetOutput(ref byte output, byte value)
    {
        if (output == value)
        {
            return false;
        }
        output = value;
        return true;
    }

    MiaSecondaryPortOutputState CaptureOutputState() => new(
        _register40,
        _register48,
        _register80);

    byte ThrowUnsupportedRead()
    {
        UnsupportedReadCount++;
        throw new InvalidOperationException(
            $"Unrecovered secondary-port read selector 0x{_selectedRegister:x2}.");
    }

    void Invoke(Action action)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            action();
        }
        else
        {
            _worker.Invoke(action);
        }
    }

    TResult Invoke<TResult>(Func<TResult> function)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            return function();
        }
        return _worker.Invoke(function);
    }
}
