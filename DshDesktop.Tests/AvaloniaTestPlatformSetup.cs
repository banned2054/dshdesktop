using NUnit.Framework;

namespace DshDesktop.Tests;

[SetUpFixture]
public sealed class AvaloniaTestPlatformSetup
{
    [OneTimeSetUp]
    public void InitializeAvaloniaPlatformBeforeAllTests()
    {
        MainWindowViewModelTests.EnsureAvaloniaPlatform();
    }
}
