using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace SlotsInstaller
{
    // 1.0.15: Windows refuses a file path longer than 259 characters (a folder: 247) unless long paths are turned on
    // for the whole PC (LongPathsEnabled). The Manager's manifest and app.config already opt in, which is all an app
    // can do. 7-Zip writes such paths anyway, so a deep mod in a deep game folder failed after extracting with
    // "Could not find file". Staging names are short now, and paths are checked before anything is extracted so the
    // error says what to do.
    internal static class LongPaths
    {
        internal const string HelpTopic = "A path is too long";
        internal static bool? EnabledOverride;   // checks: act as if long paths are on or off
        internal static int? FileLimitOverride;  // checks: the test workspace is deeper than any real game folder
        private static bool? enabled;

        internal static int FileLimit { get { return FileLimitOverride ?? 259; } }
        internal static int FolderLimit { get { return FileLimit - 12; } }

        internal static bool Enabled
        {
            get
            {
                if (EnabledOverride.HasValue) return EnabledOverride.Value;
                if (!enabled.HasValue)
                {
                    bool on = false;
                    try
                    {
                        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
                            on = key != null && Convert.ToInt32(key.GetValue("LongPathsEnabled", 0)) == 1;
                    }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                    enabled = on;
                }
                return enabled.Value;
            }
        }

        // The first full path that is too long (the file or its folder); null when all fit or long paths are on.
        internal static string TooLong(IEnumerable<string> files)
        {
            if (Enabled) return null;
            foreach (string file in files)
                if (file.Length > FileLimit || (Path.GetDirectoryName(file) ?? "").Length > FolderLimit) return file;
            return null;
        }
        internal static void Check(IEnumerable<string> files)
        {
            string file = TooLong(files);
            if (file != null) throw new IOException(Message(file));
        }
        internal static string Message(string file)
        {
            return "This path is too long for Windows (" + file.Length + " characters; the limit is " + FileLimit + " while Windows long paths are off): " + file + ". Turn on long paths and try again (Help: \"" + HelpTopic + "\").";
        }
    }
}
