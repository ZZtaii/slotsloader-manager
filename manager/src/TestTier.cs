using System;

namespace SlotsInstaller
{
    // 1.0.21: two test tiers. A plain test.ps1 runs the lean set (core paths and data safety, no review PNGs);
    // test.ps1 -Full sets SLOTSLOADER_TEST_FULL=1 and every check also runs its full-only cases and saves its PNGs.
    // SLOTSLOADER_RENDER=1 saves the PNGs in a lean run too. The release gate is test.ps1 -Full -RealArchives.
    internal static class TestTier
    {
        internal const string FullVariable = "SLOTSLOADER_TEST_FULL", RenderVariable = "SLOTSLOADER_RENDER";
        internal static bool Full { get { return Environment.GetEnvironmentVariable(FullVariable) == "1"; } }
        internal static bool Render { get { return Full || Environment.GetEnvironmentVariable(RenderVariable) == "1"; } }
    }
}
