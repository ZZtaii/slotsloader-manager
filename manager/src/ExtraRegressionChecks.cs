using System;
using System.IO;

namespace SlotsInstaller
{
    internal static class ExtraRegressionChecks
    {
        internal static int Run(string output)
        {
            string root=Path.GetFullPath(output);
            Safety.NoReparseChain(root);
            root=Path.Combine(root,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(root);
            int failed=CompositionRegressionChecks.Run(root);
            try { InstallImprovementChecks.Run(Path.Combine(root,"install-improvements")); } catch(Exception ex) { failed++; Console.WriteLine("FAIL install improvements: "+ex); }
            try { foreach(string line in ArchiveRegressionChecks.Run(Path.Combine(root,"archives"))) Console.WriteLine(line); } catch(Exception ex) { failed++; Console.WriteLine("FAIL archive regression: "+ex); }
            try { foreach(string line in LibraryRegressionChecks.Run(Path.Combine(root,"libraries"))) Console.WriteLine(line); } catch(Exception ex) { failed++; Console.WriteLine("FAIL library regression: "+ex); }
            try { LoaderResultChecks.Run(Path.Combine(root,"loader-results")); } catch(Exception ex) { failed++; Console.WriteLine("FAIL loader mapping: "+ex); }
            failed+=RecoveryRegressionChecks.Run(Path.Combine(root,"recovery"));
            failed+=CommandLineRegressionChecks.Run(Path.Combine(root,"cli"));
            Console.WriteLine("Extra regression failures: "+failed+". Fixtures: "+root);
            return failed==0 ? 0 : 1;
        }
    }
}
