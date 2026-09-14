// SPDX-License-Identifier: MIT

using Mia.Emulator.Presentation;

namespace AvrCore.Tests;

internal sealed class PresentationTestObservable : ObservableObject
{
    private int _value;

    public int Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}
