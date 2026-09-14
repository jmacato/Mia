// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaPersistenceTests
{
    [Fact]
    public void SnapshotSerializationRoundTripsSimOverlayAndRejectsOldVersions()
    {
        MiaPersistenceSnapshot snapshot = new(
            MiaPersistenceSnapshot.CurrentVersion,
            [new(0x7f10, 0x6f3c, [0x03, 0x06, 0x91])]);

        string text = MiaPersistence.Serialize(snapshot);
        MiaPersistenceSnapshot restored = Assert.IsType<MiaPersistenceSnapshot>(
            MiaPersistence.Deserialize(text));

        SimFileOverlay simFile = Assert.Single(restored.SimFiles);
        Assert.Equal(0x7f10, simFile.Parent);
        Assert.Equal(0x6f3c, simFile.Id);
        Assert.Equal(new byte[] { 0x03, 0x06, 0x91 }, simFile.Data);
        Assert.Null(MiaPersistence.Deserialize(text.Replace(
            $"\"Version\":{MiaPersistenceSnapshot.CurrentVersion}",
            "\"Version\":0",
            StringComparison.Ordinal)));
    }

    [Fact]
    public void PersistenceKeyIsStableAndFirmwareSpecific()
    {
        string first = MiaPersistence.CreateKey([0x00, 0x01, 0x02]);

        Assert.Equal(first, MiaPersistence.CreateKey([0x00, 0x01, 0x02]));
        Assert.NotEqual(first, MiaPersistence.CreateKey([0x00, 0x01, 0x03]));
        Assert.StartsWith("00000003-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineRestoresAndRecapturesSmsFileOverlay()
    {
        SimFileOverlay sms = CreateSmsOverlay();
        MiaPersistenceSnapshot snapshot = new(
            MiaPersistenceSnapshot.CurrentVersion,
            [sms]);

        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            powerKeyReleaseCycle: long.MaxValue,
            persistenceSnapshot: snapshot);

        MiaPersistenceCapture capture = machine.CapturePersistenceSnapshot();
        SimFileOverlay restored = Assert.Single(capture.Snapshot.SimFiles);
        Assert.Equal(sms.Parent, restored.Parent);
        Assert.Equal(sms.Id, restored.Id);
        Assert.Equal(sms.Data, restored.Data);
    }

    [Fact]
    public async Task CoordinatorFlushesChangedSmsOverlayToStore()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            powerKeyReleaseCycle: long.MaxValue);
        var store = new MiaPersistenceTestsRecordingPersistenceStore();
        var coordinator = new MiaPersistenceCoordinator(new(
            "test-phone",
            store,
            MiaPersistenceSnapshot.Empty));
        coordinator.MarkLoaded(machine);

        machine.SimCard!.ApplyOverlay([CreateSmsOverlay()]);
        await coordinator.FlushAsync(machine);

        MiaPersistenceSnapshot saved = Assert.IsType<MiaPersistenceSnapshot>(
            store.SavedSnapshot);
        Assert.Equal("test-phone", store.SavedKey);
        Assert.Single(saved.SimFiles);
    }

    [Fact]
    public void CoordinatorDrainsSynchronousStoreWithoutAnAsyncContinuation()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            powerKeyReleaseCycle: long.MaxValue);
        var store = new MiaPersistenceTestsRecordingPersistenceStore();
        var coordinator = new MiaPersistenceCoordinator(new(
            "test-phone",
            store,
            MiaPersistenceSnapshot.Empty));
        coordinator.MarkLoaded(machine);
        var saved = false;
        coordinator.SnapshotSaved += _ => saved = true;

        machine.SimCard!.ApplyOverlay([CreateSmsOverlay()]);
        coordinator.ScheduleSave(machine);

        Assert.True(saved);
        Assert.NotNull(store.SavedSnapshot);
    }

    [Fact]
    public async Task CoordinatorCoalescesScheduleSaveDuringAGenuinelyAsyncSave()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            powerKeyReleaseCycle: long.MaxValue);
        var store = new MiaPersistenceTestsGatedPersistenceStore();
        var coordinator = new MiaPersistenceCoordinator(new(
            "test-phone",
            store,
            MiaPersistenceSnapshot.Empty));
        coordinator.MarkLoaded(machine);
        var savedCount = 0;
        coordinator.SnapshotSaved += _ => Interlocked.Increment(ref savedCount);

        machine.SimCard!.ApplyOverlay([CreateSmsOverlay()]);
        coordinator.ScheduleSave(machine);
        await store.FirstSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // A second change arrives while the first SaveAsync call is
        // genuinely still pending. It must coalesce into the same in-flight
        // drain instead of starting a second, overlapping SaveAsync call.
        machine.SimCard!.ApplyOverlay([CreateSmsOverlay()]);
        coordinator.ScheduleSave(machine);
        Assert.Equal(1, Volatile.Read(ref store.EnteredCount));

        store.Release();
        await coordinator.FlushAsync(machine);

        Assert.False(store.ObservedOverlap);
        Assert.Equal(2, Volatile.Read(ref store.EnteredCount));
        Assert.Equal(2, savedCount);
    }

    [Fact]
    public async Task MachinePublishesPersistenceChangesFromTheSimOwner()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            powerKeyReleaseCycle: long.MaxValue);
        var changed = new TaskCompletionSource();
        long publishedVersion = -1;
        machine.PersistenceChanged += version =>
        {
            publishedVersion = version;
            changed.TrySetResult();
        };

        machine.SimCard!.ApplyOverlay([CreateSmsOverlay()]);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        Assert.Equal(machine.PersistenceVersion, publishedVersion);
    }

    static SimFileOverlay CreateSmsOverlay()
    {
        var card = new SwSimCard();
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x7f, 0x10);
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x6f, 0x3c);
        byte[] record = Enumerable.Repeat((byte)0xff, 176).ToArray();
        record[0] = 0x03;
        record[1] = 0x06;
        record[2] = 0x91;
        Send(card, [0xa0, 0xdc, 0x01, 0x04, 0xb0, .. record]);
        return Assert.Single(card.CreateOverlay());
    }

    static byte[] Send(SwSimCard card, params byte[] bytes)
    {
        var responseBytes = new List<byte>();
        foreach (byte value in bytes)
        {
            SimCardResponse? response = card.Transmit(value);
            if (response is not null)
            {
                responseBytes.AddRange(response.Value.Data);
            }
        }
        return responseBytes.ToArray();
    }
}
