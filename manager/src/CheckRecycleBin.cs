using System;
using System.Collections.Generic;
using System.IO;

namespace SlotsInstaller
{
    // 1.0.1: uninstall sends an outfit or replacement without a library source to the Recycle Bin. The self-checks
    // must never touch the user's real one, so every headless check run starts with this stand-in (Program.Main): it
    // moves what it "recycles" into Data\check-recycle-bin. --delete-check swaps in its own stub.
    internal sealed class CheckRecycleBin : IRecycleBin
    {
        internal readonly List<string> Recycled = new List<string>();
        internal string Folder { get { return Path.Combine(AppConfig.DataDir, "check-recycle-bin"); } }
        public string Unavailable(string path, bool folder, long bytes) { return null; }
        public void Recycle(string path, bool folder)
        {
            lock (Recycled)
            {
                Recycled.Add(path);
                string target = Path.Combine(Folder, Recycled.Count + " " + Guid.NewGuid().ToString("N").Substring(0, 6) + " " + Path.GetFileName(path.TrimEnd('\\')));
                Directory.CreateDirectory(Folder);
                if (folder) Directory.Move(path, target); else File.Move(path, target);
            }
        }
        public void DeletePermanently(string path, bool folder) { if (folder) Directory.Delete(path, true); else File.Delete(path); }
    }
}
