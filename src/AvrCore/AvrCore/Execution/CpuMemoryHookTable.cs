// SPDX-License-Identifier: MIT

namespace AvrCore.Execution;

internal sealed class CpuMemoryHookTable<T>
    where T : class
{
    const int PageShift = 10;
    const int PageLength = 1 << PageShift;
    const int PageMask = PageLength - 1;

    readonly T?[][] _pages;

    public CpuMemoryHookTable(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Length = length;
        _pages = new T?[(length + PageMask) >> PageShift][];
    }

    public int Length { get; }

    public T? this[int address]
    {
        get
        {
            ValidateAddress(address);
            var page = _pages[address >> PageShift];
            return page is null ? null : page[address & PageMask];
        }
        set
        {
            ValidateAddress(address);
            var pageIndex = address >> PageShift;
            var page = _pages[pageIndex];
            if (page is null)
            {
                if (value is null)
                {
                    return;
                }
                page = new T?[PageLength];
                _pages[pageIndex] = page;
            }
            page[address & PageMask] = value;
        }
    }

    void ValidateAddress(int address)
    {
        if ((uint)address >= (uint)Length)
        {
            throw new IndexOutOfRangeException();
        }
    }
}
