// SPDX-License-Identifier: MIT

using Mia.Emulator.Presentation;
using Xunit;

namespace AvrCore.Tests;

public sealed class PresentationDelegateCommandTests
{
    [Fact]
    public void ExecuteHonorsEnabledState()
    {
        var executionCount = 0;
        var isEnabled = false;
        var command = new DelegateCommand(
            () => executionCount++,
            () => isEnabled);

        command.Execute(parameter: null);
        isEnabled = true;
        command.Execute(parameter: null);

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void OwnerCanPublishEnabledStateChanges()
    {
        var command = new DelegateCommand(() => { });
        var notificationCount = 0;
        command.CanExecuteChanged += (_, _) => notificationCount++;

        command.NotifyCanExecuteChanged();

        Assert.Equal(1, notificationCount);
    }
}
