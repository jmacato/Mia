// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

/// <summary>
/// Legacy Bluetooth authentication and pairing primitives recovered from the
/// modem firmware functions at 010ab4b4..010abc9c.
/// </summary>
internal static class BluetoothLegacyCrypto
{
    const int BlockLength = 16;
    const int BluetoothAddressLength = 6;
    const int RoundCount = 8;

    static readonly byte[] Exponential = CreateExponentialTable();
    static readonly byte[] Logarithm = CreateLogarithmTable();

    internal static void Authenticate(
        ReadOnlySpan<byte> linkKey,
        ReadOnlySpan<byte> random,
        ReadOnlySpan<byte> address,
        BluetoothLegacyAuthenticationOutput output)
    {
        RequireLength(linkKey, BlockLength, nameof(linkKey));
        RequireLength(random, BlockLength, nameof(random));
        RequireLength(address, BluetoothAddressLength, nameof(address));
        RequireLength(output.Response, 4, "response");
        RequireLength(
            output.AuthenticatedCipheringOffset,
            12,
            "authenticatedCipheringOffset");

        Span<byte> result = stackalloc byte[BlockLength];
        E1(linkKey, random, address, result);
        result[..4].CopyTo(output.Response);
        result[4..].CopyTo(output.AuthenticatedCipheringOffset);
        result.Clear();
    }

    internal static void DeriveCombinationKeyPart(
        ReadOnlySpan<byte> random,
        ReadOnlySpan<byte> address,
        Span<byte> keyPart)
    {
        RequireLength(random, BlockLength, nameof(random));
        RequireLength(address, BluetoothAddressLength, nameof(address));
        RequireLength(keyPart, BlockLength, nameof(keyPart));

        Span<byte> key = stackalloc byte[BlockLength];
        random.CopyTo(key);
        key[^1] ^= BluetoothAddressLength;

        Span<byte> input = stackalloc byte[BlockLength];
        Repeat(address, input);
        keyPart.Clear();
        Transform(strengthened: true, key, input, keyPart);
        key.Clear();
        input.Clear();
    }

    internal static void DeriveInitializationKey(
        ReadOnlySpan<byte> random,
        ReadOnlySpan<byte> pinAndAddress,
        Span<byte> initializationKey)
    {
        RequireLength(random, BlockLength, nameof(random));
        if (pinAndAddress.IsEmpty || pinAndAddress.Length > BlockLength)
        {
            throw new ArgumentException(
                "Bluetooth initialization-key material must contain 1..16 bytes.",
                nameof(pinAndAddress));
        }
        RequireLength(
            initializationKey,
            BlockLength,
            nameof(initializationKey));

        Span<byte> key = stackalloc byte[BlockLength];
        Repeat(pinAndAddress, key);
        Span<byte> input = stackalloc byte[BlockLength];
        random.CopyTo(input);
        input[^1] ^= checked((byte)pinAndAddress.Length);
        initializationKey.Clear();
        Transform(strengthened: true, key, input, initializationKey);
        key.Clear();
        input.Clear();
    }

    static void E1(
        ReadOnlySpan<byte> linkKey,
        ReadOnlySpan<byte> random,
        ReadOnlySpan<byte> address,
        Span<byte> result)
    {
        random.CopyTo(result);
        Transform(strengthened: false, linkKey, random, result);

        Span<byte> mixed = stackalloc byte[BlockLength];
        for (var index = 0; index < BlockLength; index++)
        {
            mixed[index] = Add(result[index], address[index % address.Length]);
            result[index] = 0;
        }

        Span<byte> transformedKey = stackalloc byte[BlockLength];
        linkKey.CopyTo(transformedKey);
        transformedKey[0] = Add(transformedKey[0], 0xe9);
        transformedKey[1] ^= 0xe5;
        transformedKey[2] = Add(transformedKey[2], 0xdf);
        transformedKey[3] ^= 0xc1;
        transformedKey[4] = Add(transformedKey[4], 0xb3);
        transformedKey[5] ^= 0xa7;
        transformedKey[6] = Add(transformedKey[6], 0x95);
        transformedKey[7] ^= 0x83;
        transformedKey[8] ^= 0xe9;
        transformedKey[9] = Add(transformedKey[9], 0xe5);
        transformedKey[10] ^= 0xdf;
        transformedKey[11] = Add(transformedKey[11], 0xc1);
        transformedKey[12] ^= 0xb3;
        transformedKey[13] = Add(transformedKey[13], 0xa7);
        transformedKey[14] ^= 0x95;
        transformedKey[15] = Add(transformedKey[15], 0x83);

        Transform(strengthened: true, transformedKey, mixed, result);
        transformedKey.Clear();
        mixed.Clear();
    }

    static void Transform(
        bool strengthened,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> input,
        Span<byte> outputAccumulator)
    {
        Span<byte> state = stackalloc byte[BlockLength];
        input.CopyTo(state);
        Span<byte> schedule = stackalloc byte[BlockLength + 1];
        key.CopyTo(schedule);
        for (var index = 0; index < BlockLength; index++)
        {
            schedule[^1] ^= key[index];
        }

        Span<byte> roundKey = stackalloc byte[BlockLength];
        var roundKeyNumber = 1;
        for (var round = 0; round < RoundCount; round++)
        {
            // Firmware mode 1 performs the Bluetooth Ar' input injection
            // before its third encryption round (010ab5ce..010ab624).
            if (strengthened && round == 2)
            {
                MixRoundKey(state, input);
            }

            NextRoundKey(schedule, ref roundKeyNumber, roundKey);
            MixRoundKey(state, roundKey);
            NextRoundKey(schedule, ref roundKeyNumber, roundKey);
            ApplyNonlinearLayer(state, roundKey);

            for (var layer = 0; layer < 3; layer++)
            {
                ApplyPseudoHadamardLayer(state);
                ApplyArmenianShuffle(state);
            }
            ApplyPseudoHadamardLayer(state);
        }

        NextRoundKey(schedule, ref roundKeyNumber, roundKey);
        MixRoundKey(state, roundKey);
        for (var index = 0; index < BlockLength; index++)
        {
            outputAccumulator[index] ^= state[index];
        }

        roundKey.Clear();
        schedule.Clear();
        state.Clear();
    }

    static void NextRoundKey(
        Span<byte> schedule,
        ref int roundKeyNumber,
        Span<byte> roundKey)
    {
        if (roundKeyNumber == 1)
        {
            schedule[..BlockLength].CopyTo(roundKey);
        }
        else
        {
            RotateSchedule(schedule);
            BuildRoundKey(schedule, roundKeyNumber, roundKey);
        }
        roundKeyNumber++;
    }

    static void RotateSchedule(Span<byte> schedule)
    {
        for (var index = 0; index < schedule.Length; index++)
        {
            schedule[index] = (byte)(
                schedule[index] << 3 |
                schedule[index] >> 5);
        }
    }

    static void BuildRoundKey(
        ReadOnlySpan<byte> schedule,
        int roundKeyNumber,
        Span<byte> roundKey)
    {
        for (var index = 0; index < BlockLength; index++)
        {
            int scheduleIndex =
                (index + roundKeyNumber - 1) % schedule.Length;
            int constantIndex =
                (roundKeyNumber * schedule.Length + index + 1) & 0xff;
            roundKey[index] = Add(
                schedule[scheduleIndex],
                Exponential[Exponential[constantIndex]]);
        }
    }

    static void MixRoundKey(Span<byte> state, ReadOnlySpan<byte> roundKey)
    {
        for (var index = 0; index < BlockLength; index += 4)
        {
            state[index] ^= roundKey[index];
            state[index + 1] = Add(state[index + 1], roundKey[index + 1]);
            state[index + 2] = Add(state[index + 2], roundKey[index + 2]);
            state[index + 3] ^= roundKey[index + 3];
        }
    }

    static void ApplyNonlinearLayer(
        Span<byte> state,
        ReadOnlySpan<byte> roundKey)
    {
        for (var index = 0; index < BlockLength; index += 4)
        {
            state[index] = Add(Exponential[state[index]], roundKey[index]);
            state[index + 1] =
                (byte)(Logarithm[state[index + 1]] ^ roundKey[index + 1]);
            state[index + 2] =
                (byte)(Logarithm[state[index + 2]] ^ roundKey[index + 2]);
            state[index + 3] =
                Add(Exponential[state[index + 3]], roundKey[index + 3]);
        }
    }

    static void ApplyPseudoHadamardLayer(Span<byte> state)
    {
        for (var index = 0; index < BlockLength; index += 2)
        {
            state[index + 1] = Add(state[index + 1], state[index]);
            state[index] = Add(state[index], state[index + 1]);
        }
    }

    static void ApplyArmenianShuffle(Span<byte> state)
    {
        byte saved = state[0];
        state[0] = state[8];
        state[8] = state[10];
        state[10] = state[14];
        state[14] = state[4];
        state[4] = state[2];
        state[2] = state[12];
        state[12] = saved;

        saved = state[1];
        state[1] = state[11];
        state[11] = state[13];
        state[13] = state[7];
        state[7] = state[5];
        state[5] = saved;

        (state[3], state[15]) = (state[15], state[3]);
    }

    static void Repeat(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = source[index % source.Length];
        }
    }

    static byte Add(byte left, int right) =>
        unchecked((byte)(left + right));

    static byte[] CreateExponentialTable()
    {
        var table = new byte[byte.MaxValue + 1];
        var value = 1;
        for (var index = 0; index < table.Length; index++)
        {
            table[index] = unchecked((byte)value);
            value = value * 45 % 257;
        }
        return table;
    }

    static byte[] CreateLogarithmTable()
    {
        var table = new byte[byte.MaxValue + 1];
        for (var index = 0; index < Exponential.Length; index++)
        {
            table[Exponential[index]] = checked((byte)index);
        }
        return table;
    }

    static void RequireLength(
        ReadOnlySpan<byte> value,
        int expected,
        string parameterName)
    {
        if (value.Length != expected)
        {
            throw new ArgumentException(
                $"Bluetooth crypto input must contain exactly {expected} bytes.",
                parameterName);
        }
    }
}
