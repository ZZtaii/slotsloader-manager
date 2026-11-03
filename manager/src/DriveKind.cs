using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SlotsInstaller
{
    // 1.0.16 (speed, the user: "does this cause issues for users on slower PCs?"): reads that overlap (an archive hashed
    // while it is extracted, game files hashed by several threads) are quick on an SSD and slow a hard drive, whose head
    // then jumps between them. They overlap only where Windows says the drive has no seek penalty (as its own defrag/trim
    // choice does); a hard drive, a network share or an unknown answer keeps one read after another, as before.
    internal static class DriveKind
    {
        private static readonly Dictionary<string, bool> known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        internal static Func<string, bool?> Override;   // checks: "ssd"/"hdd" for any path

        // True when the drive holding `path` is solid state (no seek penalty).
        internal static bool IsSolidState(string path)
        {
            if (Override != null) { bool? forced = Override(path); if (forced.HasValue) return forced.Value; }
            string volume = Volume(path);
            if (volume == null) return false;
            lock (known)
            {
                bool solid;
                if (known.TryGetValue(volume, out solid)) return solid;
                solid = NoSeekPenalty(volume) == true;
                known[volume] = solid;
                return solid;
            }
        }

        // Threads for hashing many files: one on a hard drive or with two cores or fewer, else cores minus one, at most 4
        // (a weak CPU keeps a core for the window).
        internal static int HashThreads(string path)
        {
            int cores = Environment.ProcessorCount - 1;
            if (cores < 2 || !IsSolidState(path)) return 1;
            return Math.Min(4, cores);
        }

        // "\\?\Volume{...}" of the volume holding path (mounted folders included), or null (a share, an error).
        private static string Volume(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                var mount = new StringBuilder(1024);
                if (!GetVolumePathName(full, mount, mount.Capacity)) return null;
                var name = new StringBuilder(1024);
                if (!GetVolumeNameForVolumeMountPoint(mount.ToString(), name, name.Capacity)) return null;
                return name.ToString().TrimEnd('\\');
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }

        private static bool? NoSeekPenalty(string volume)
        {
            try
            {
                // No access rights are needed to ask a volume about its device (works without administrator rights).
                using (SafeFileHandle h = CreateFile(volume, 0, 3 /* share read+write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    var query = new StoragePropertyQuery { PropertyId = 7 /* StorageDeviceSeekPenaltyProperty */, QueryType = 0 };
                    SeekPenaltyDescriptor answer; uint returned;
                    if (!DeviceIoControl(h, 0x2D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, ref query, (uint)Marshal.SizeOf(query), out answer, (uint)Marshal.SizeOf(typeof(SeekPenaltyDescriptor)), out returned, IntPtr.Zero)) return null;
                    if (returned < 9) return null;
                    return !answer.IncursSeekPenalty;
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct StoragePropertyQuery { public uint PropertyId; public uint QueryType; public byte AdditionalParameters; }
        [StructLayout(LayoutKind.Sequential)] private struct SeekPenaltyDescriptor { public uint Version; public uint Size; [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref StoragePropertyQuery input, uint inputSize, out SeekPenaltyDescriptor output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumePathName(string path, StringBuilder volume, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder name, int length);
    }
}
