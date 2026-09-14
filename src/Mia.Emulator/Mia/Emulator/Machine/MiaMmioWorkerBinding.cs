// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Machine;

/// <summary>
/// Routes a synchronous AVR MMIO register facade to its device owner. The AVR
/// is blocked for the request/reply, so workers never race core-visible memory.
/// Explicitly single-threaded browser builds keep the hooks inline.
/// </summary>
internal static class MiaMmioWorkerBinding
{
    public static void Bind(Cpu cpu, MiaWorker worker, params int[] addresses)
    {
#if !BROWSER || BROWSER_THREADS
        if (!worker.HasDedicatedThread)
        {
            return;
        }
        foreach (var address in addresses)
        {
            BindAddress(cpu, worker, address);
        }
#endif
    }

    public static void BindRange(
        Cpu cpu,
        MiaWorker worker,
        int firstAddress,
        int lastAddress)
    {
#if !BROWSER || BROWSER_THREADS
        if (!worker.HasDedicatedThread)
        {
            return;
        }
        for (var address = firstAddress; address <= lastAddress; address++)
        {
            BindAddress(cpu, worker, address);
        }
#endif
    }

    /// <summary>
    /// Routes only writes to the device owner. Use this for atomic read-only
    /// register snapshots which the owner publishes directly to the bus.
    /// </summary>
    public static void BindWrites(
        Cpu cpu,
        MiaWorker worker,
        params int[] addresses)
    {
#if !BROWSER || BROWSER_THREADS
        if (!worker.HasDedicatedThread)
        {
            return;
        }
        foreach (var address in addresses)
        {
            BindWriteAddress(cpu, worker, address);
        }
#endif
    }

    /// <summary>
    /// Routes a read to its owner and carries one deferred-effect bit back in
    /// the reply. The post-reply action runs on the AVR caller only after the
    /// device worker has returned, avoiding a synchronous worker cycle.
    /// </summary>
    public static void BindReadWithPostReply(
        Cpu cpu,
        MiaWorker worker,
        int address,
        Func<bool> takePostReplyRequest,
        Action postReply)
    {
        var read = cpu.ReadHooks[address] ?? throw new InvalidOperationException(
            $"MMIO address 0x{address:x} has no read hook to bind.");
        if (!worker.HasDedicatedThread)
        {
            cpu.ReadHooks[address] = hookAddress =>
            {
                byte value = read(hookAddress);
                if (takePostReplyRequest())
                {
                    postReply();
                }
                return value;
            };
            return;
        }
        cpu.ReadHooks[address] = hookAddress =>
        {
#if BROWSER && !BROWSER_THREADS
            var reply = (
                Value: read(hookAddress),
                PostReply: takePostReplyRequest());
#else
            worker.RecordBoundMmioRead();
            var reply = worker.Invoke(() => (
                Value: read(hookAddress),
                PostReply: takePostReplyRequest()));
#endif
            if (reply.PostReply)
            {
                postReply();
            }
            return reply.Value;
        };
    }

#if !BROWSER || BROWSER_THREADS
    static void BindAddress(Cpu cpu, MiaWorker worker, int address)
    {
        if (cpu.ReadHooks[address] is { } read)
        {
            cpu.ReadHooks[address] = hookAddress =>
            {
                worker.RecordBoundMmioRead();
                return worker.Invoke(() => read(hookAddress));
            };
        }
        if (cpu.WriteHooks[address] is { } write)
        {
            BindWriteAddress(cpu, worker, address, write);
        }
    }

    static void BindWriteAddress(
        Cpu cpu,
        MiaWorker worker,
        int address,
        CpuMemoryWriteHook? write = null)
    {
        write ??= cpu.WriteHooks[address] ??
            throw new InvalidOperationException(
                $"MMIO address 0x{address:x} has no write hook to bind.");
        cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
        {
            worker.RecordBoundMmioWrite();
            return worker.Invoke(
                () => write(value, oldValue, hookAddress, mask));
        };
    }
#endif
}
