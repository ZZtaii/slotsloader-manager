namespace SlotsInstaller
{
    internal static class TestRunner
    {
        private static int Main(string[] args) { return IntegrationTests.Run(args[0]); }
    }
}
