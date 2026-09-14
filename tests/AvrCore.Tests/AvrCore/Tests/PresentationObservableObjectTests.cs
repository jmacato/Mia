// SPDX-License-Identifier: MIT

using Mia.Emulator.Presentation;
using Xunit;

namespace AvrCore.Tests;

public sealed class PresentationObservableObjectTests
{
    [Fact]
    public void SetPropertyRaisesOnlyWhenTheValueChanges()
    {
        var observable = new PresentationTestObservable();
        var changedProperties = new List<string?>();
        observable.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        observable.Value = 7;
        observable.Value = 7;

        Assert.Equal([nameof(PresentationTestObservable.Value)], changedProperties);
    }
}
