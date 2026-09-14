// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

internal sealed class AsicInterruptRouter
{
    public const int RouteDataAddress = 0x0820;
    public const int PhysicalSourceAddress = 0x0824;

    readonly Cpu _cpu;
    readonly AsicInterruptController _controller;
    readonly MiaWorker? _worker;
    readonly Dictionary<byte, AsicInterruptRoute> _routes = [];
    readonly int[] _publishedHighPrioritySources = new int[byte.MaxValue + 1];

    public AsicInterruptRouter(
        Cpu cpu,
        AsicInterruptController controller,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _controller = controller;
        _worker = worker;
        cpu.WriteHooks[PhysicalSourceAddress] =
            (value, oldValue, address, mask) => Invoke(
                () => ProgramRoute(value, oldValue, address, mask));
    }

    public IReadOnlyDictionary<byte, AsicInterruptRoute> Routes => _routes;

    public event Action<AsicInterruptRoute>? RouteProgrammed;

    public bool TryResolveProcessDestination(byte physicalSource, out byte processDestination)
    {
        byte resolved = 0;
        var found = Invoke(() => TryResolveProcessDestinationCore(
            physicalSource,
            out resolved));
        processDestination = resolved;
        return found;
    }

    bool TryResolveProcessDestinationCore(
        byte physicalSource,
        out byte processDestination)
    {
        if (_routes.TryGetValue(physicalSource, out var route))
        {
            processDestination = route.ProcessDestination;
            return true;
        }

        processDestination = 0;
        return false;
    }

    public bool TryResolveHighPrioritySource(byte physicalSource, out byte interruptSource)
    {
        var encoded = Volatile.Read(
            ref _publishedHighPrioritySources[physicalSource]);
        if (encoded != 0)
        {
            interruptSource = (byte)(encoded - 1);
            return true;
        }

        interruptSource = 0;
        return false;
    }

    public static bool TryMapProcessToHighPrioritySource(
        byte processDestination,
        out byte interruptSource)
    {
        // The firmware's fixed high-priority vector wrappers prove this mapping
        // for the six time-generator/PH processes. Route records contain these
        // process IDs, not the source bytes read by the primary ISR at 0x0816.
        if (processDestination is >= 0x07 and <= 0x0c)
        {
            interruptSource = (byte)(0x25 - processDestination);
            return true;
        }

        interruptSource = 0;
        return false;
    }

    public bool RaiseHighPriority(byte physicalSource)
        => RaiseHighPriorityCore(physicalSource);

    public int RaiseHighPriorityPair(
        byte firstPhysicalSource,
        byte secondPhysicalSource) =>
        RaiseHighPriorityPairCore(firstPhysicalSource, secondPhysicalSource);

    bool RaiseHighPriorityCore(byte physicalSource)
    {
        if (!TryResolveHighPrioritySource(physicalSource, out var interruptSource))
        {
            return false;
        }

        _controller.RaiseHighPriority(interruptSource);
        return true;
    }

    int RaiseHighPriorityPairCore(
        byte firstPhysicalSource,
        byte secondPhysicalSource)
    {
        var firstResolved = TryResolveHighPrioritySourceCore(
            firstPhysicalSource,
            out var firstSource);
        var secondResolved = TryResolveHighPrioritySourceCore(
            secondPhysicalSource,
            out var secondSource);
        if (firstResolved && secondResolved)
        {
            _controller.RaiseHighPriorityPair(firstSource, secondSource);
            return 2;
        }
        if (firstResolved)
        {
            _controller.RaiseHighPriority(firstSource);
            return 1;
        }
        if (secondResolved)
        {
            _controller.RaiseHighPriority(secondSource);
            return 1;
        }
        return 0;
    }

    bool TryResolveHighPrioritySourceCore(
        byte physicalSource,
        out byte interruptSource)
    {
        return TryResolveHighPrioritySource(physicalSource, out interruptSource);
    }

    bool ProgramRoute(byte physicalSource, byte _, int __, byte ___)
    {
        // Firmware writes zero here while staging each record. A nonzero write
        // commits the four bytes at 0x0820-0x0823 for that physical source.
        if (physicalSource == 0)
        {
            return false;
        }

        var route = new AsicInterruptRoute(
            physicalSource,
            _cpu.Data[RouteDataAddress + 1],
            _cpu.Data[RouteDataAddress],
            _cpu.Data[RouteDataAddress + 2],
            _cpu.Data[RouteDataAddress + 3]);

        // All records emitted by this firmware use a byte pair whose sum is
        // 2 modulo 256, followed by ff 00. Refuse an unfamiliar format rather
        // than turning an arbitrary staging write into an interrupt route.
        if (unchecked((byte)(route.CheckByte + route.ProcessDestination)) != 2 ||
            route.Marker != 0xff || route.Reserved != 0)
        {
            return false;
        }

        _routes[physicalSource] = route;
        Volatile.Write(
            ref _publishedHighPrioritySources[physicalSource],
            TryMapProcessToHighPrioritySource(
                route.ProcessDestination,
                out var interruptSource)
                ? interruptSource + 1
                : 0);
        RouteProgrammed?.Invoke(route);
        return false;
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
