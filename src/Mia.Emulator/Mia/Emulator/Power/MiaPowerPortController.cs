// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Power;

/// <summary>
/// Firmware-visible power, port, and ADC companion on the primary I2C bus.
/// Only contracts recovered from the native PORTHND code are implemented;
/// unknown selectors fail closed.
/// </summary>
internal sealed class MiaPowerPortController
{
    public const byte WriteAddress = 0x90;
    public const byte ReadAddress = 0x91;
    public const byte RevisionCommand = 0xa0;
    public const byte OnOffStatusCommand = 0xa1;
    public const byte PortStatusCommand = 0xa2;
    public const byte PortA3OutputCommand = 0xa3;
    public const byte PortA4OutputCommand = 0xa4;
    public const byte PortA5OutputCommand = 0xa5;
    public const byte PortA6OutputCommand = 0xa6;
    public const byte PortA7OutputCommand = 0xa7;
    public const byte AdcControlCommand = 0xa8;
    public const byte PortA9OutputCommand = 0xa9;
    public const byte PowerControlCommand = 0xaa;
    public const byte InterruptMaskCommand = 0xab;
    public const byte PortB0OutputCommand = 0xb0;
    public const byte FirstAdcSampleCommand = 0xb1;
    public const byte SecondaryAdcSampleCommand = 0xb2;
    public const byte AlternateAdcSampleCommand = 0xb3;
    public const byte BatteryAdcSampleCommand = 0xb4;
    public const byte ChargingCurrentAdcSampleCommand = 0xb5;
    public const byte BatteryTemperatureAdcSampleCommand = 0xb6;
    public const byte PowerKeyLevel = 0x01;
    public const byte PowerKeyEdge = 0x02;
    public const byte ExternalPowerLevel = 0x04;
    public const byte PortStatusReady = 0x20;
    public const byte AdcChannelMask = 0xe0;
    public const byte ExternalPowerAdcChannel = 0x00;
    public const byte BatteryAdcChannel = 0x20;
    public const byte ChargingCurrentAdcChannel = 0x40;
    public const byte BatteryTemperatureAdcChannel = 0x60;

    // The firmware distinguishes legacy revisions below 0xf4. The exact
    // production-board byte has not been recovered, so the hardware profile
    // uses the last legacy code point and lets the firmware execute its own
    // compatibility path.
    public const byte LegacySiliconRevision = 0xf3;

    // Native calibration maps raw 0x58 to 370 (centivolts), representing a
    // normal 3.70 V battery at the companion's firmware-visible ADC boundary.
    public const byte DefaultBatteryAdcSample = 0x58;

    // The charged path can request the retained battery-voltage result while
    // ADC channel zero is selected. It still enters the native battery-voltage
    // conversion, so expose the same 3.70 V profile through that bank.
    public const byte DefaultExternalPowerAdcSample = DefaultBatteryAdcSample;

    // Native B5 calibration is raw * 4.668831 + 7. Raw 6A therefore models
    // approximately 502 mA of positive charging current.
    public const byte DefaultChargingCurrentAdcSample = 0x6a;

    // Native B6 calibration is (raw * 25) / 10 + 5. Raw 0A therefore models
    // a nominal battery temperature of 30 degrees C.
    public const byte DefaultBatteryTemperatureAdcSample = 0x0a;

    readonly AsicInterruptController _interruptController;
    readonly MiaWorker? _worker;
    byte _portA3Output;
    byte _portA4Output;
    byte _portA5Output;
    byte _portA6Output;
    byte _portA7Output;
    byte _adcControl;
    byte _portA9Output;
    byte _powerControl;
    byte _portB0Output;
    byte _selectedCommand;
    byte _interruptMask = 0xff;
    bool _powerPressed;
    bool _powerKeyEdgePending;
    bool _externalPowerConnected;
    bool _externalPowerEdgePending;
    bool _chargingActive;
    bool _interruptAsserted;

    public MiaPowerPortController(
        AsicInterruptController interruptController,
        bool powerPressedInitially = false,
        bool externalPowerConnectedInitially = false,
        byte siliconRevision = LegacySiliconRevision,
        byte batteryAdcSample = DefaultBatteryAdcSample,
        byte externalPowerAdcSample = DefaultExternalPowerAdcSample,
        byte chargingCurrentAdcSample = DefaultChargingCurrentAdcSample,
        byte batteryTemperatureAdcSample = DefaultBatteryTemperatureAdcSample,
        MiaWorker? worker = null)
    {
        _interruptController = interruptController;
        _worker = worker;
        _powerPressed = powerPressedInitially;
        _powerKeyEdgePending = powerPressedInitially;
        _externalPowerConnected = externalPowerConnectedInitially;
        SiliconRevision = siliconRevision;
        BatteryAdcSample = batteryAdcSample;
        ExternalPowerAdcSample = externalPowerAdcSample;
        ChargingCurrentAdcSample = chargingCurrentAdcSample;
        BatteryTemperatureAdcSample = batteryTemperatureAdcSample;
    }

    public byte SiliconRevision { get; }

    public byte BatteryAdcSample { get; }

    public byte ExternalPowerAdcSample { get; }

    public byte ChargingCurrentAdcSample { get; }

    public byte BatteryTemperatureAdcSample { get; }

    public bool PowerPressed => Invoke(() => _powerPressed);

    public bool ExternalPowerConnected => Invoke(() => _externalPowerConnected);

    public bool ChargingActive => Invoke(() => _chargingActive);

    public byte InterruptMask => _interruptMask;

    public byte AdcControl => _adcControl;

    public byte PowerControl => _powerControl;

    public MiaPowerPortOutputState OutputState =>
        Invoke(CaptureOutputState);

    public event Action<MiaPowerPortOutputState>? OutputStateChanged;

    public event Action<bool>? ChargingStateChanged;

    public long ReadCount { get; private set; }

    public long WriteCount { get; private set; }

    public long UnsupportedReadCount { get; private set; }

    public long RejectedWriteCount { get; private set; }

    public long InterruptCount { get; private set; }

    public long PortStatusReadCount { get; private set; }

    public long OnOffStatusReadCount { get; private set; }

    public long VoltageAdcReadCount { get; private set; }

    public long ChargingCurrentAdcReadCount { get; private set; }

    public long BatteryTemperatureAdcReadCount { get; private set; }

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

        var outputChanged = false;
        switch (_selectedCommand)
        {
            case PortA3OutputCommand:
                outputChanged = SetOutput(ref _portA3Output, value);
                break;
            case PortA4OutputCommand:
                outputChanged = SetOutput(ref _portA4Output, value);
                break;
            case PortA5OutputCommand:
                outputChanged = SetOutput(ref _portA5Output, value);
                break;
            case PortA6OutputCommand:
                outputChanged = SetOutput(ref _portA6Output, value);
                break;
            case PortA7OutputCommand:
                outputChanged = SetOutput(ref _portA7Output, value);
                break;
            case AdcControlCommand:
                _adcControl = value;
                break;
            case PortA9OutputCommand:
                outputChanged = SetOutput(ref _portA9Output, value);
                break;
            case PowerControlCommand:
                outputChanged = SetOutput(ref _powerControl, value);
                break;
            case InterruptMaskCommand:
                _interruptMask = value;
                UpdateInterrupt();
                break;
            case PortB0OutputCommand:
                outputChanged = SetOutput(ref _portB0Output, value);
                break;
            default:
                RejectedWriteCount++;
                throw new InvalidOperationException(
                    $"Unrecovered power-port write selector 0x{_selectedCommand:x2}.");
        }
        WriteCount++;
        if (outputChanged)
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

        _selectedCommand = payload[0];
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

    static bool SetOutput(ref byte output, byte value)
    {
        if (output == value)
        {
            return false;
        }
        output = value;
        return true;
    }

    MiaPowerPortOutputState CaptureOutputState() => new(
        _portA3Output,
        _portA4Output,
        _portA5Output,
        _portA6Output,
        _portA7Output,
        _portA9Output,
        _powerControl,
        _portB0Output);

    public byte ReadByte(byte address) => Invoke(() => ReadByteCore(address));

    byte ReadByteCore(byte address)
    {
        if (address != ReadAddress)
        {
            UnsupportedReadCount++;
            throw new InvalidOperationException(
                $"Unsupported power-port read address 0x{address:x2}.");
        }

        ReadCount++;
        return _selectedCommand switch
        {
            RevisionCommand => SiliconRevision,
            OnOffStatusCommand => ReadOnOffStatus(),
            PortStatusCommand => ReadPortStatus(),
            AdcControlCommand => _adcControl,
            PowerControlCommand => _powerControl,
            FirstAdcSampleCommand => ReadAdcSample(),
            SecondaryAdcSampleCommand => ReadAdcSample(),
            AlternateAdcSampleCommand => ReadAdcSample(),
            BatteryAdcSampleCommand => ReadAdcSample(),
            ChargingCurrentAdcSampleCommand => ReadChargingCurrentAdcSample(),
            BatteryTemperatureAdcSampleCommand => ReadBatteryTemperatureAdcSample(),
            _ => ThrowUnsupportedRead(),
        };
    }

    public void PressPower() => Invoke(PressPowerCore);

    void PressPowerCore()
    {
        if (_powerPressed)
        {
            return;
        }
        _powerPressed = true;
        _powerKeyEdgePending = true;
        UpdateInterrupt();
    }

    public void ReleasePower() => Invoke(ReleasePowerCore);

    void ReleasePowerCore()
    {
        if (!_powerPressed)
        {
            return;
        }
        _powerPressed = false;
        _powerKeyEdgePending = true;
        UpdateInterrupt();
    }

    public void SetExternalPowerConnected(bool connected) =>
        Invoke(() => SetExternalPowerConnectedCore(connected));

    void SetExternalPowerConnectedCore(bool connected)
    {
        if (_externalPowerConnected == connected)
        {
            return;
        }
        _externalPowerConnected = connected;
        _externalPowerEdgePending = true;
        if (!connected)
        {
            SetChargingActive(false);
        }
        UpdateInterrupt();
    }

    byte ReadOnOffStatus()
    {
        OnOffStatusReadCount++;
        var value = _powerPressed ? (byte)0 : PowerKeyLevel;
        if (_powerKeyEdgePending)
        {
            value |= PowerKeyEdge;
        }
        if (_externalPowerEdgePending)
        {
            value |= ExternalPowerLevel;
        }
        _powerKeyEdgePending = false;
        _externalPowerEdgePending = false;
        UpdateInterrupt();
        return value;
    }

    byte ReadPortStatus()
    {
        PortStatusReadCount++;
        var value = _externalPowerConnected ? ExternalPowerLevel : (byte)0;
        if (SiliconRevision >= 0xf4)
        {
            value |= PortStatusReady;
        }
        return value;
    }

    byte ReadAdcSample()
    {
        VoltageAdcReadCount++;
        byte channel = (byte)(_adcControl & AdcChannelMask);
        if (channel == BatteryAdcChannel)
        {
            return BatteryAdcSample;
        }
        if (channel == ExternalPowerAdcChannel && _externalPowerConnected)
        {
            return ExternalPowerAdcSample;
        }
        if (channel == BatteryTemperatureAdcChannel)
        {
            return BatteryTemperatureAdcSample;
        }
        return ThrowUnsupportedRead(
            $"ADC selector 0x{_selectedCommand:x2} read with unrecovered " +
            $"channel 0x{channel:x2}.");
    }

    byte ReadChargingCurrentAdcSample()
    {
        ChargingCurrentAdcReadCount++;
        byte channel = (byte)(_adcControl & AdcChannelMask);
        if (_externalPowerConnected && channel == ChargingCurrentAdcChannel)
        {
            SetChargingActive(true);
            return ChargingCurrentAdcSample;
        }
        return ThrowUnsupportedRead(
            $"Charging-current ADC selector 0x{_selectedCommand:x2} read " +
            $"with unrecovered channel 0x{channel:x2}.");
    }

    byte ReadBatteryTemperatureAdcSample()
    {
        BatteryTemperatureAdcReadCount++;
        byte channel = (byte)(_adcControl & AdcChannelMask);
        if (channel == BatteryTemperatureAdcChannel)
        {
            return BatteryTemperatureAdcSample;
        }
        return ThrowUnsupportedRead(
            $"Battery-temperature ADC selector 0x{_selectedCommand:x2} read " +
            $"with unrecovered channel 0x{channel:x2}.");
    }

    byte ThrowUnsupportedRead(string? message = null)
    {
        UnsupportedReadCount++;
        throw new InvalidOperationException(message ??
            $"Unrecovered power-port read selector 0x{_selectedCommand:x2}.");
    }

    void SetChargingActive(bool active)
    {
        if (_chargingActive == active)
        {
            return;
        }
        _chargingActive = active;
        ChargingStateChanged?.Invoke(active);
    }

    void UpdateInterrupt()
    {
        var shouldAssert =
            (_powerKeyEdgePending &&
             (_interruptMask & PowerKeyEdge) == 0) ||
            (_externalPowerEdgePending &&
             (_interruptMask & ExternalPowerLevel) == 0);
        if (shouldAssert && !_interruptAsserted)
        {
            _interruptController.RaiseHighPriority(
                AsicInterruptController.External1Source);
            InterruptCount++;
        }
        _interruptAsserted = shouldAssert;
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
