using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // Data\test-session.json: a temporary install made by "Test in game" (removed in 2.3.0). Only the record of an
    // unfinished test from an older version is still read, so it can be kept as a normal install.
    public sealed class TestSessionRecord
    {
        public string Format = "sf6-test-session-v1";
        public string Mode = "";              // slot or replace
        public string ReplaceModId = "";
        public string OutfitId = "", OutfitCharacter = "", OutfitName = "", OutfitFolder = "";
        public string Name = "";
        public string Phase = "waiting";      // waiting (for the game to start), running, ended
        public string StartedUtc = "", LaunchedUtc = "";
        public List<string> Files = new List<string>();
        public Dictionary<string, string> RestoreConfig = new Dictionary<string, string>();   // REFramework keys to put back
    }

    internal static class TestSession
    {
        internal static string FilePath(string dataDir) { return Path.Combine(dataDir, "test-session.json"); }
        internal static TestSessionRecord Load(string dataDir)
        {
            try
            {
                string f = FilePath(dataDir);
                if (!File.Exists(f)) return null;
                var r = Json.Read<TestSessionRecord>(f);
                return r != null && r.Format == "sf6-test-session-v1" ? r : null;
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }
        internal static void Save(string dataDir, TestSessionRecord record) { Directory.CreateDirectory(dataDir); DurableFile.WriteJson(FilePath(dataDir), record); }
        internal static void Clear(string dataDir) { string f = FilePath(dataDir); if (File.Exists(f)) File.Delete(f); }

        // An unfinished test from an older version: its install stays as a normal install (a replacement loses its
        // test mark; an extra outfit is already a normal outfit), REFramework's logging keys it turned on go back,
        // and the record is deleted. Returns a line for the log and status.
        internal static string FinishLeftover(ReplaceEngine engine, TestSessionRecord record)
        {
            string message;
            if (record.Mode == InstallModes.Replace && !String.IsNullOrEmpty(record.ReplaceModId) && engine.LoadState().Mods.Any(m => m.Id == record.ReplaceModId))
            {
                engine.KeepTest(record.ReplaceModId);
                message = "Kept '" + record.Name + "' from an unfinished test as a normal replacement.";
            }
            else if (record.Mode == InstallModes.Slot) message = "Kept '" + record.Name + "' from an unfinished test as a normal extra outfit.";
            else message = "Cleared the unfinished test of '" + record.Name + "'; its install was already gone.";
            foreach (var kv in record.RestoreConfig.ToList()) LooseLoader.Write(engine.GameRoot, kv.Key, kv.Value, engine.ConfigBackupDir);
            if (record.RestoreConfig.Count > 0) message += " REFramework logging settings restored (" + String.Join(", ", record.RestoreConfig.Keys) + ").";
            Clear(engine.DataDir);
            return message;
        }
    }
}
