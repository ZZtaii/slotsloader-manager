using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    internal static class LoaderContractChecks
    {
        internal static void Run(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Loose);
            var original = ReplaceChecks.Snapshot(f.Game);
            string metadata = Path.Combine(f.Mod("tool"), "Tool Pack", "modinfo.ini");
            string header = "name=SF6 Slots Loader\r\nauthor=Wael3rd\r\nversion=1.20.0\r\n";
            foreach (string value in new[] { null, "", "1", "0", "-1", "two", "2.0", "2147483648", "2", " 2 ", "3" })
            {
                File.WriteAllText(metadata, header + (value == null ? "" : "sLoTsLoAdErCoNtRaCt=" + value + "\r\n"));
                bool accepted = value == "2" || value == " 2 " || value == "3";
                ReplacePlan plan = ReplaceChecks.Check(f, "tool");
                bool refused = plan.Items.Any(i => i.Text.Contains("This is upstream's Slots Loader"));
                ReplaceChecks.Require(refused != accepted && plan.CanInstall == accepted, "contract '" + (value ?? "missing") + "': " + String.Join(" | ", plan.Items.Select(i => i.Text)));
            }
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "preparation leaves game unchanged");
            ReplaceChecks.Require(!LoaderCostumePak.HonoursContract(null) && !LoaderCostumePak.HonoursContract(new ModComponent { Metadata = null }), "missing component metadata");

            File.WriteAllText(metadata, header + "SlotsloaderContract=2\r\n");
            ReplaceMod installed = ReplaceChecks.Install(f, "tool", "Contract loader");
            ReplaceChecks.Require(File.Exists(Path.Combine(f.Game, LoaderCostumePak.PluginPath.Replace('/', '\\'))) && !File.Exists(Path.Combine(f.Game, "SF6_SlotsLoader_Ext.dll")), "contract loader installs without extension");
            f.Engine.Uninstall(installed.Id);
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "contract loader uninstall restores game");

            File.WriteAllText(metadata, "name=Slotsloader Fork\r\nauthor=ZZtaii, Wael3rd\r\nversion=1.10.4\r\n");
            ReplaceChecks.Require(ReplaceChecks.Check(f, "tool").CanInstall, "fork without contract remains accepted");

            // A sibling component must not grant compatibility to the plugin's own component.
            File.WriteAllText(metadata, header + "SlotsloaderContract=1\r\n");
            ReplaceChecks.Put(f.Mod("tool"), "Other/modinfo.ini", Encoding.UTF8.GetBytes("name=Other\r\nSlotsloaderContract=2\r\n"));
            ReplaceChecks.Put(f.Mod("tool"), "Other/reframework/autorun/other.lua", Encoding.UTF8.GetBytes("-- fixture"));
            ReplaceChecks.Require(!ReplaceChecks.Check(f, "tool").CanInstall, "sibling contract must not bypass plugin refusal");
        }
    }
}
