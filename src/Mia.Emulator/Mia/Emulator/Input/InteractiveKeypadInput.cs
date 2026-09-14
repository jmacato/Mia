// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Input;

/// <summary>
/// Adapts host button edges to the physical keypad matrix. A host release that
/// arrives before the firmware has completed its matching MMIO debounce reads
/// remains physically held until those reads, so draining stdin cannot collapse
/// an intentional click into an unobservable zero-instruction pulse.
/// </summary>
internal sealed class InteractiveKeypadInput
{
    readonly AsicKeypad _keypad;
    readonly MiaPowerPortController _powerPorts;
    readonly AsicInterruptController _interrupts;
    readonly Action<Action>? _scheduleAfterTick;
    readonly Dictionary<InteractiveKeypadContact, InteractiveKeypadInputContactState> _states = [];
    bool _hasReadyRelease;
    bool _releaseFlushScheduled;
    bool _postTickFlushRequestAvailable;

    public InteractiveKeypadInput(
        AsicKeypad keypad,
        MiaPowerPortController powerPorts,
        AsicInterruptController interrupts,
        Action<Action>? scheduleAfterTick = null)
    {
        _keypad = keypad;
        _powerPorts = powerPorts;
        _interrupts = interrupts;
        _scheduleAfterTick = scheduleAfterTick;
        keypad.ContactObserved += OnContactObserved;
        keypad.ScanCompleted += OnScanCompleted;
    }

    public event Action<InteractiveKeypadContact>? DeferredReleaseApplied;

    public InteractiveKeypadTransition SetKey(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed)
    {
        if (!AsicKeypad.IsValidContact(scanMask, rowMask))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scanMask),
                $"Invalid keypad contact scan=0x{scanMask:x2}, row=0x{rowMask:x2}.");
        }
        if (secondaryScanMask is byte secondary &&
            (!AsicKeypad.IsValidContact(secondary, rowMask) ||
             secondary == scanMask ||
             scanMask == 0x0f ||
             secondary == 0x0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(secondaryScanMask),
                $"Invalid secondary keypad contact scan=0x{secondary:x2}, " +
                $"row=0x{rowMask:x2}.");
        }
        return SetContact(
            new(scanMask, rowMask, secondaryScanMask),
            pressed);
    }

    public InteractiveKeypadTransition SetKey(
        byte scanMask,
        byte rowMask,
        bool pressed) =>
        SetKey(scanMask, rowMask, secondaryScanMask: null, pressed);

    public InteractiveKeypadTransition SetPower(bool pressed) =>
        SetContact(new(
            AsicKeypad.NoPowerScanMask,
            AsicKeypad.NoPowerRowMask,
            Power: true), pressed);

    public void FlushDeferredReleases()
    {
        _releaseFlushScheduled = false;
        if (!_hasReadyRelease)
        {
            return;
        }

        _hasReadyRelease = false;
        foreach (var (contact, state) in _states.ToArray())
        {
            if (!state.PhysicallyPressed ||
                !state.ReleaseRequested ||
                !HasBeenObserved(contact, state))
            {
                continue;
            }
            ApplyRelease(contact, state);
            DeferredReleaseApplied?.Invoke(contact);
        }
    }

    public bool TakePostTickFlushRequest()
    {
        var requested = _postTickFlushRequestAvailable;
        _postTickFlushRequestAvailable = false;
        return requested;
    }

    void OnScanCompleted()
    {
        if (!_hasReadyRelease || _releaseFlushScheduled)
        {
            return;
        }
        if (_scheduleAfterTick is null)
        {
            _releaseFlushScheduled = true;
            _postTickFlushRequestAvailable = true;
            return;
        }

        _releaseFlushScheduled = true;
        _scheduleAfterTick(FlushDeferredReleases);
    }

    InteractiveKeypadTransition SetContact(
        InteractiveKeypadContact contact,
        bool pressed)
    {
        InteractiveKeypadInputContactState state = GetContactState(contact);
        return pressed
            ? PressContact(contact, state)
            : ReleaseContact(contact, state);
    }

    InteractiveKeypadInputContactState GetContactState(
        InteractiveKeypadContact contact)
    {
        if (_states.TryGetValue(contact, out var state))
        {
            return state;
        }
        state = new InteractiveKeypadInputContactState();
        _states.Add(contact, state);
        return state;
    }

    InteractiveKeypadTransition PressContact(
        InteractiveKeypadContact contact,
        InteractiveKeypadInputContactState state)
    {
        if (state.PhysicallyPressed)
        {
            state.ReleaseRequested = false;
            return InteractiveKeypadTransition.Unchanged;
        }

        state.PhysicallyPressed = true;
        state.PrimaryObservations = 0;
        state.SecondaryObservations = 0;
        state.ReleaseRequested = false;
        if (contact.Power)
        {
            _powerPorts.PressPower();
        }
        if (contact.ScanMask != 0)
        {
            _keypad.Press(
                contact.ScanMask,
                contact.RowMask,
                contact.SecondaryScanMask);
            _interrupts.RaiseHighPriority(
                AsicInterruptController.KeypadSource);
        }
        return InteractiveKeypadTransition.Applied;
    }

    InteractiveKeypadTransition ReleaseContact(
        InteractiveKeypadContact contact,
        InteractiveKeypadInputContactState state)
    {
        if (!state.PhysicallyPressed)
        {
            return InteractiveKeypadTransition.Unchanged;
        }
        if (!HasBeenObserved(contact, state))
        {
            state.ReleaseRequested = true;
            return InteractiveKeypadTransition.Deferred;
        }

        ApplyRelease(contact, state);
        return InteractiveKeypadTransition.Applied;
    }

    void OnContactObserved(byte scanMask, byte rowMask)
    {
        foreach (var (contact, state) in _states)
        {
            if (!state.PhysicallyPressed || contact.RowMask != rowMask)
            {
                continue;
            }

            if (contact.ScanMask == scanMask)
            {
                state.PrimaryObservations = IncrementSaturating(
                    state.PrimaryObservations);
            }
            if (contact.SecondaryScanMask == scanMask)
            {
                state.SecondaryObservations = IncrementSaturating(
                    state.SecondaryObservations);
            }
            _hasReadyRelease |=
                state.ReleaseRequested && HasBeenObserved(contact, state);
        }
    }

    static bool HasBeenObserved(
        InteractiveKeypadContact contact,
        InteractiveKeypadInputContactState state)
    {
        if (contact.SecondaryScanMask is not byte secondary)
        {
            var required = contact.ScanMask == 0x0f ? 1 : 2;
            return state.PrimaryObservations >= required;
        }

        return state.PrimaryObservations >= RequiredChordObservations(
                contact.ScanMask,
                secondary) &&
            state.SecondaryObservations >= RequiredChordObservations(
                secondary,
                contact.ScanMask);
    }

    void ApplyRelease(
        InteractiveKeypadContact contact,
        InteractiveKeypadInputContactState state)
    {
        state.PhysicallyPressed = false;
        state.PrimaryObservations = 0;
        state.SecondaryObservations = 0;
        state.ReleaseRequested = false;
        if (contact.ScanMask != 0)
        {
            _keypad.Release(
                contact.ScanMask,
                contact.RowMask,
                contact.SecondaryScanMask);
            _interrupts.RaiseHighPriority(
                AsicInterruptController.KeypadSource);
        }
        if (contact.Power)
        {
            _powerPorts.ReleasePower();
        }
    }

    static byte IncrementSaturating(byte value) =>
        value == byte.MaxValue ? value : (byte)(value + 1);

    static int RequiredChordObservations(byte scanMask, byte otherScanMask) =>
        ScanOrdinal(scanMask) < ScanOrdinal(otherScanMask) ? 3 : 2;

    static int ScanOrdinal(byte scanMask) => scanMask switch
    {
        0x0e => 0,
        0x0d => 1,
        0x0b => 2,
        0x07 => 3,
        _ => throw new ArgumentOutOfRangeException(
            nameof(scanMask),
            $"Scan 0x{scanMask:x2} is not part of the ordinary matrix."),
    };
}
