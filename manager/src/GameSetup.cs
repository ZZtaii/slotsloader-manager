using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 26 Nov 2026 (the user: "im hoping we can simplify things to install in one click for newer people ... feels like they
    // gotta get reframework, then slotsloader then native core now"): one click installs whatever of the three is
    // missing, in that order, each from its pinned and verified zip (a library copy or Data\downloads first, else the
    // download). No review step: these are the Manager's own approved builds, installed the way the CLI's update-install
    // does. Something another tool installed is left to it (REFramework or Native Core from the external mod manager, a
    // Slotsloader that is there already); Slotsloader switched off with the outfit mods is not installed again.
    internal sealed class SetupNeeds
    {
        // CoreUpdate: the Native Core the Manager installed is older than the approved one (CoreRecordId: its record,
        // updated in place).
        internal bool ReFramework, ReFrameworkUpdate, Loader, Core, CoreUpdate;
        internal string CoreRecordId, CoreFrom;
        // Tools the Manager installed whose record is turned off on the Installed page: not installed again (a reinstall
        // of the same zip keeps the record off), the status says to turn them back on.
        internal List<string> TurnedOff = new List<string>();
        internal LoaderPin CorePin;   // the Native Core to install (UpdateCheck.CoreInstallPin)
        internal bool Any { get { return ReFramework || Loader || Core || CoreUpdate; } }
        internal List<string> Names()
        {
            var names = new List<string>();
            if (ReFramework) names.Add("REFramework");
            if (Loader) names.Add("Slotsloader");
            if (Core || CoreUpdate) names.Add("Native Core");
            return names;
        }
        // "REFramework, Slotsloader and Native Core"
        internal string Text() { return GameSetup.Join(Names()); }
    }

    internal sealed class SetupResult
    {
        internal List<string> Installed = new List<string>();
        internal string Failed;          // the step that failed and why (the ones before it stay installed)
        internal string Message
        {
            get
            {
                string done = Installed.Count == 0 ? "" : "Installed " + GameSetup.Join(Installed) + ".";
                if (Failed == null) return done + " Start SF6 to use them.";
                return (done.Length > 0 ? done + " " : "") + Failed;
            }
        }
    }

    internal static class GameSetup
    {
        internal static string Join(List<string> names)
        {
            if (names.Count <= 1) return names.Count == 0 ? "" : names[0];
            return String.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        // What one click would install. state: the Manager's records (null when unreadable: then the loader counts as
        // missing only by the game folder).
        internal static SetupNeeds Needs(ToolStatus tools, ReplaceState state, FluffyInstalls fluffy) { return Needs(tools, state, fluffy, LoaderRelease.CorePin); }
        internal static SetupNeeds Needs(ToolStatus tools, ReplaceState state, FluffyInstalls fluffy, LoaderPin corePin)
        {
            var n = new SetupNeeds { CorePin = corePin };
            if (tools == null || !tools.GameFound) return n;
            n.ReFramework = tools.ReFrameworkOffer;
            n.ReFrameworkUpdate = n.ReFramework && tools.ReFrameworkFile;
            bool parked = state != null && state.ModsOff != null && !String.IsNullOrEmpty(state.ModsOff.LoaderId);
            n.Loader = !tools.SlotsLoader && !parked;
            bool coreByFluffy = fluffy != null && fluffy.OwnersOf(LoaderFlags.CorePlugin).Count > 0;
            n.Core = !tools.NativeCore && !coreByFluffy && corePin != null;
            // 26 Nov 2026: the Manager's own core, older than the one to install: updated in place
            ReplaceMod record = tools.NativeCore && !coreByFluffy && corePin != null ? InstalledCore.Record(state) : null;
            if (record != null && UpdateCheck.CoreUpdate(ReleaseOffer.Approved(corePin, UpdateProducts.Core), InstalledCore.Version(record)) != null)
            {
                n.CoreUpdate = true; n.CoreRecordId = record.Id; n.CoreFrom = InstalledCore.Version(record);
            }
            // A missing tool whose record of this Manager is only turned off: the user turns it back on
            if (n.ReFramework && !tools.ReFrameworkFile && TurnedOff(state, "dinput8.dll")) { n.ReFramework = false; n.ReFrameworkUpdate = false; n.TurnedOff.Add("REFramework"); }
            if (n.Loader && TurnedOff(state, LoaderCostumePak.PluginPath)) { n.Loader = false; n.TurnedOff.Add("Slotsloader"); }
            if (n.Core && TurnedOff(state, LoaderFlags.CorePlugin)) { n.Core = false; n.TurnedOff.Add("Native Core"); }
            return n;
        }
        private static bool TurnedOff(ReplaceState state, string path)
        {
            if (state == null) return false;
            Func<ReplaceMod, bool> holds = m => m != null && m.Files != null && m.Files.Any(f => String.Equals(PakHash.Normalize(f.Path ?? "").TrimStart('/'), path, StringComparison.OrdinalIgnoreCase));
            return state.Mods.Any(m => !m.Enabled && holds(m)) && !state.Mods.Any(m => m.Enabled && holds(m));
        }

        // Runs on a worker thread. loaderPin: the Slotsloader build to install (the approved one, MainForm.CurrentInstallPin).
        internal static SetupResult Run(ReplaceEngine engine, SetupNeeds needs, LoaderPin loaderPin, IEnumerable<string> libraries, string sevenZip, string downloads)
        {
            var result = new SetupResult();
            List<string> libs = (libraries ?? new string[0]).ToList();
            var steps = new List<KeyValuePair<string, Func<LoaderFile>>>();
            if (needs.ReFramework) { ReFrameworkPin p = ReFrameworkRelease.Pin; steps.Add(new KeyValuePair<string, Func<LoaderFile>>(p.Label, delegate { return ReFrameworkRelease.Fetch(p, downloads); })); }
            if (needs.Loader) steps.Add(new KeyValuePair<string, Func<LoaderFile>>("Slotsloader " + UpdateCheck.ShortVersion(loaderPin.Version), delegate { return LoaderRelease.Fetch(loaderPin, libs, downloads, true); }));
            LoaderPin corePin = needs.CorePin ?? LoaderRelease.CorePin;
            if ((needs.Core || needs.CoreUpdate) && corePin != null) steps.Add(new KeyValuePair<string, Func<LoaderFile>>("Native Core " + corePin.Version, delegate { return LoaderRelease.Fetch(corePin, libs, downloads, true); }));
            foreach (var step in steps)
            {
                OperationContext.ThrowIfCancellationRequested();
                try
                {
                    LoaderFile file = step.Value();
                    Program.LogLine("Set up: " + step.Key + " from the " + file.SourceLabel + ": " + file.Path + " · SHA-256 " + file.Sha256);
                    ModPackage package = PackageReader.Load(file.Path, sevenZip);
                    CompositionPlan composition = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
                    if (!composition.CanInstall) throw new IOException(String.Join(" ", composition.Errors));
                    ReplacePlan plan;
                    // an update of the Manager's own Native Core keeps its record (and its place)
                    string replaceId = needs.CoreUpdate && ReplaceEngine.HasCorePlugin(composition.Files.Keys) ? needs.CoreRecordId : null;
                    ReplaceMod mod = ReplacePlanner.PrepareAndInstall(composition, sevenZip, engine, package.Name, false, replaceId, replaceId != null, out plan);
                    if (LoaderCostumePak.IsLoaderMod(mod)) UnownedSlotsFlag.Ensure(engine.GameRoot);
                    Program.LogLine("Set up: installed " + step.Key + " (" + ReplacePlanner.N(mod.Files.Count, "file") + ").");
                    result.Installed.Add(replaceId != null && !String.IsNullOrEmpty(needs.CoreFrom) ? step.Key + " (was " + UpdateCheck.ShortVersion(needs.CoreFrom) + ")" : step.Key);
                }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException || ex is OperationCanceledException) throw;
                    Program.Log(ex);
                    result.Failed = step.Key + " was not installed: " + ex.Message;
                    break;
                }
            }
            return result;
        }
    }
}
