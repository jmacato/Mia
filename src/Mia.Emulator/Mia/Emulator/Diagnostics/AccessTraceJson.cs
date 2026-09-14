// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceJson
{
    public static void Write(
        Stream destination,
        AccessTraceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The aggregate destination must be writable.",
                nameof(destination));
        }

        using var writer = new Utf8JsonWriter(
            destination,
            new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("schema", snapshot.Schema);
        WriteWindow(writer, snapshot.Window);
        WriteClock(writer, snapshot.Clock);
        WriteCapture(writer, snapshot.Capture);
        WriteStorage(writer, snapshot.Storage);
        writer.WriteString(
            "timeBinEncoding",
            "sparse-event-bins-with-held-value-forward-fill");
        WriteReconciliation(writer, snapshot.Reconciliation);
        WriteAddresses(writer, snapshot.Addresses);
        writer.WriteEndObject();
        writer.Flush();
    }

    static void WriteWindow(
        Utf8JsonWriter writer,
        AccessTraceWindow window)
    {
        writer.WriteStartObject("window");
        writer.WriteNumber("startCycle", window.StartCycle);
        writer.WriteNumber("endCycleExclusive", window.EndCycleExclusive);
        writer.WriteNumber("durationCycles", window.DurationCycles);
        writer.WriteNumber("binCount", window.BinCount);
        writer.WriteEndObject();
    }

    static void WriteClock(
        Utf8JsonWriter writer,
        AccessTraceClockInfo clock)
    {
        writer.WriteStartObject("clock");
        writer.WriteString("machineCycleDomain", clock.MachineCycleDomain);
        writer.WriteNumber("machineCyclesPerSecond", clock.MachineCyclesPerSecond);
        writer.WriteNumber("avrCyclesPerSecond", clock.AvrCyclesPerSecond);
        writer.WriteNumber("avrToMachineNumerator", clock.AvrToMachineNumerator);
        writer.WriteNumber("avrToMachineDenominator", clock.AvrToMachineDenominator);
        writer.WriteNumber("avrToMachineOffset", clock.AvrToMachineOffset);
        writer.WriteEndObject();
    }

    static void WriteCapture(
        Utf8JsonWriter writer,
        AccessTraceCaptureInfo capture)
    {
        writer.WriteStartObject("capture");
        writer.WriteBoolean("stopped", capture.Stopped);
        WriteNullableInt64(writer, "stoppedAtCycle", capture.StoppedAtCycle);
        writer.WriteEndObject();
    }

    static void WriteStorage(
        Utf8JsonWriter writer,
        AccessTraceStorageMetrics storage)
    {
        writer.WriteStartObject("storage");
        writer.WriteNumber("addressCount", storage.AddressCount);
        writer.WriteNumber("timeBinCellCount", storage.TimeBinCellCount);
        writer.WriteNumber("binValueCellCount", storage.BinValueCellCount);
        writer.WriteNumber(
            "valueHistogramCellCount",
            storage.ValueHistogramCellCount);
        writer.WriteNumber("transitionCellCount", storage.TransitionCellCount);
        writer.WriteNumber(
            "programCounterCellCount",
            storage.ProgramCounterCellCount);
        writer.WriteNumber("binValueOverflowCount", storage.BinValueOverflowCount);
        writer.WriteNumber(
            "valueHistogramOverflowCount",
            storage.ValueHistogramOverflowCount);
        writer.WriteNumber(
            "transitionOverflowCount",
            storage.TransitionOverflowCount);
        writer.WriteNumber(
            "programCounterOverflowCount",
            storage.ProgramCounterOverflowCount);
        writer.WriteEndObject();
    }

    static void WriteReconciliation(
        Utf8JsonWriter writer,
        IReadOnlyList<AccessTraceReconciliationSnapshot> items)
    {
        writer.WriteStartArray("reconciliation");
        foreach (AccessTraceReconciliationSnapshot item in items)
        {
            writer.WriteStartObject();
            writer.WriteString("bus", Format(item.Bus));
            writer.WriteString("operation", Format(item.Operation));
            writer.WriteNumber("callbackCount", item.CallbackCount);
            writer.WriteNumber("aggregatedCount", item.AggregatedCount);
            writer.WriteNumber("outsideWindowCount", item.OutsideWindowCount);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static void WriteAddresses(
        Utf8JsonWriter writer,
        IReadOnlyList<AccessTraceAddressSnapshot> addresses)
    {
        writer.WriteStartArray("addresses");
        foreach (AccessTraceAddressSnapshot address in addresses)
        {
            writer.WriteStartObject();
            writer.WriteString("bus", Format(address.Key.Bus));
            writer.WriteNumber("address", address.Key.Address);
            writer.WriteNumber("size", address.Key.Size);
            writer.WriteString(
                "instructionAddressUnit",
                address.Key.Bus == AccessTraceBus.ArmMmio ? "byte" : "word");
            WriteNullableByte(writer, "device", address.Key.Device);
            WriteNullableByte(writer, "register", address.Key.Register);
            writer.WriteNumber("totalCount", address.TotalCount);
            writer.WriteNumber("readCount", address.ReadCount);
            writer.WriteNumber("writeCount", address.WriteCount);
            writer.WriteNumber("changeCount", address.ChangeCount);
            writer.WriteNumber("firstCycle", address.FirstCycle);
            writer.WriteNumber("lastCycle", address.LastCycle);
            WriteOperations(writer, address.Operations);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static void WriteOperations(
        Utf8JsonWriter writer,
        IReadOnlyList<AccessTraceOperationSnapshot> operations)
    {
        writer.WriteStartArray("operations");
        foreach (AccessTraceOperationSnapshot operation in operations)
        {
            writer.WriteStartObject();
            writer.WriteString("operation", Format(operation.Operation));
            writer.WriteNumber("count", operation.Count);
            writer.WriteNumber("changeCount", operation.ChangeCount);
            writer.WriteNumber(
                "consumedWriteCount",
                operation.ConsumedWriteCount);
            writer.WriteNumber("firstCycle", operation.FirstCycle);
            writer.WriteNumber("lastCycle", operation.LastCycle);
            writer.WriteNumber("bitWidth", operation.BitWidth);
            writer.WriteNumber(
                "valueOverflowCount",
                operation.ValueOverflowCount);
            writer.WriteNumber(
                "transitionOverflowCount",
                operation.TransitionOverflowCount);
            writer.WriteNumber(
                "programCounterOverflowCount",
                operation.ProgramCounterOverflowCount);
            writer.WriteNumber("maskOverflowCount", operation.MaskOverflowCount);
            writer.WriteNumber(
                "binValueOverflowCount",
                operation.BinValueOverflowCount);
            WriteNullableUInt64(
                writer,
                "initialHeldValue",
                operation.InitialHeldValue);
            WriteCounts(writer, "values", operation.Values);
            WriteTransitions(writer, operation.Transitions);
            WriteBitEdges(writer, operation.BitEdges);
            WriteProgramCounters(writer, operation.ProgramCounters);
            WriteCounts(writer, "masks", operation.Masks);
            WriteTimeBins(writer, operation.TimeBins);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static void WriteCounts(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<AccessTraceCount> counts)
    {
        writer.WriteStartArray(propertyName);
        foreach (AccessTraceCount count in counts)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(count.Value);
            writer.WriteNumberValue(count.Count);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    static void WriteTransitions(
        Utf8JsonWriter writer,
        IReadOnlyList<AccessTraceTransitionCount> transitions)
    {
        writer.WriteStartArray("transitions");
        foreach (AccessTraceTransitionCount transition in transitions)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(transition.OldValue);
            writer.WriteNumberValue(transition.NewValue);
            writer.WriteNumberValue(transition.Count);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    static void WriteBitEdges(
        Utf8JsonWriter writer,
        IReadOnlyList<AccessTraceBitEdgeCount> bitEdges)
    {
        writer.WriteStartArray("bitEdges");
        foreach (AccessTraceBitEdgeCount bitEdge in bitEdges)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(bitEdge.Bit);
            writer.WriteNumberValue(bitEdge.Rises);
            writer.WriteNumberValue(bitEdge.Falls);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    static void WriteProgramCounters(
        Utf8JsonWriter writer,
        IReadOnlyList<AccessTracePcCount> programCounters)
    {
        writer.WriteStartArray("programCounters");
        foreach (AccessTracePcCount pc in programCounters)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(pc.InstructionAddress);
            writer.WriteNumberValue(pc.Count);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    static void WriteTimeBins(
        Utf8JsonWriter writer,
        IReadOnlyList<AccessTraceTimeBinSnapshot> timeBins)
    {
        writer.WriteStartArray("timeBins");
        foreach (AccessTraceTimeBinSnapshot bin in timeBins)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(bin.Index);
            writer.WriteNumberValue(bin.EventCount);
            writer.WriteNumberValue(bin.ChangeCount);
            writer.WriteNumberValue(bin.LastValue);
            writer.WriteNumberValue(bin.ValueOverflowCount);
            writer.WriteStartArray();
            foreach (AccessTraceBinValueCount value in bin.Values)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(value.Value);
                writer.WriteNumberValue(value.Count);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    static void WriteNullableByte(
        Utf8JsonWriter writer,
        string propertyName,
        byte? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    static void WriteNullableUInt64(
        Utf8JsonWriter writer,
        string propertyName,
        ulong? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    static void WriteNullableInt64(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    internal static string Format(AccessTraceBus bus) => bus switch
    {
        AccessTraceBus.Avr => "avr",
        AccessTraceBus.PrimaryI2c => "primary-i2c",
        AccessTraceBus.ArmMmio => "arm-mmio",
        _ => throw new ArgumentOutOfRangeException(nameof(bus)),
    };

    internal static string Format(AccessTraceOperation operation) =>
        operation switch
        {
            AccessTraceOperation.Read => "read",
            AccessTraceOperation.Write => "write",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
}
