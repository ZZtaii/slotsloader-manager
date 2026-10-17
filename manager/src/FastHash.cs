using System;
using System.Security.Cryptography;

namespace SlotsInstaller
{
    // 1.0.1 (speed): on .NET Framework SHA256.Create() is the pure-managed SHA256Managed (~150-200 MB/s). Windows'
    // own implementation (CNG) uses the CPU's SHA instructions and is several times faster; the digest is identical.
    // Falls back to the managed one where CNG isn't available.
    internal static class FastHash
    {
        private static bool cngFailed;
        internal static SHA256 Sha256()
        {
            if (!cngFailed)
            {
                try { return new SHA256Cng(); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; cngFailed = true; }
            }
            return new SHA256Managed();
        }
    }
}
