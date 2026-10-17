using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    internal static class CommandLineRegressionChecks
    {
        private const string Mesh="natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";
        private const string Material="natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mdf2.40";
        private sealed class CallResult { internal int ExitCode; internal string Output, Error; }

        internal static int Run(string fixtureRoot)
        {
            string full=Path.GetFullPath(fixtureRoot); Safety.NoReparseChain(full);
            string root=Path.Combine(full,"cli-"+Guid.NewGuid().ToString("N").Substring(0,12)); Directory.CreateDirectory(root);
            Console.WriteLine("CLI fixture root: "+root);
            int failed=0;
            Check("CLI help and invalid arguments return the documented exit codes",delegate { Arguments(Path.Combine(root,"args")); },ref failed);
            Check("list-options emits parseable JSON with canonical/default IDs",delegate { Options(Path.Combine(root,"options")); },ref failed);
            Check("test-mod verifies default and explicitly ordered winning files",delegate { Selections(Path.Combine(root,"selections")); },ref failed);
            Check("selection arrays and saved manifests preserve source identity",delegate { SelectionFiles(Path.Combine(root,"selection-files")); },ref failed);
            Check("composition, target and missing-source failures remain structured JSON",delegate { ValidationErrors(Path.Combine(root,"errors")); },ref failed);
            Check("JSON reports never overwrite files or write inside source/game trees",delegate { ReportSafety(Path.Combine(root,"reports")); },ref failed);
            Check("corrupt selected ZIP CRC is rejected without changing the archive",delegate { CorruptZip(Path.Combine(root,"crc")); },ref failed);
            Check("native-loader sandbox guards reject existing and overlapping paths",delegate { NativeSandboxGuards(Path.Combine(root,"sandbox")); },ref failed);
            Check("native-loader arguments enforce ANSI roundtrip and MAX_PATH limits",NativePathArguments,ref failed);
            Console.WriteLine("CLI regression checks: "+(9-failed)+" passed, "+failed+" failed.");
            return failed;
        }
        private static void Check(string name,Action test,ref int failed)
        {
            try { test(); Console.WriteLine("PASS "+name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex); }
        }
        private static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
        private static CallResult Invoke(params string[] args)
        {
            TextWriter originalOut=Console.Out, originalError=Console.Error;
            using (var output=new StringWriter()) using (var errors=new StringWriter())
            {
                try { Console.SetOut(output); Console.SetError(errors); return new CallResult {ExitCode=CommandLine.Run(args),Output=output.ToString(),Error=errors.ToString()}; }
                finally { Console.SetOut(originalOut); Console.SetError(originalError); }
            }
        }
        private static ModCheckReport Report(CallResult result,int expectedCode)
        {
            Require(result.ExitCode==expectedCode,"Unexpected CLI exit code "+result.ExitCode+". Output: "+result.Output+" Errors: "+result.Error);
            Require(String.IsNullOrWhiteSpace(result.Error),"JSON command also emitted an unexpected stderr message: "+result.Error);
            ModCheckReport report=Json.Serializer().Deserialize<ModCheckReport>(result.Output);
            Require(report!=null && report.Format=="sf6-installer-check-v1","CLI stdout was not a complete structured report.");
            return report;
        }
        private static void Put(string root,string path,string contents)
        {
            string file=Safety.Under(root,path); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file,contents,new UTF8Encoding(false));
        }
        private static string Source(string root)
        {
            string source=Path.Combine(root,"source");
            Put(source,"modinfo.ini","name=Main files\n"); Put(source,Mesh,"base mesh marker"); Put(source,Material,"base material marker");
            Put(source,"Extras/modinfo.ini","name=Blue recolor\n"); Put(source,"Extras/"+Material,"blue material marker");
            return source;
        }
        private static Dictionary<string,string> Fingerprint(string source)
        {
            return Directory.GetFiles(source,"*",SearchOption.AllDirectories).ToDictionary(f=>f,f=>NativeModCheck.HashFile(f),StringComparer.OrdinalIgnoreCase);
        }
        private static void Unchanged(string source,Dictionary<string,string> before)
        {
            Dictionary<string,string> after=Fingerprint(source);
            Require(before.Count==after.Count && before.All(f=>after.ContainsKey(f.Key) && after[f.Key]==f.Value),"CLI testing changed its source fixture.");
        }
        private static void Arguments(string root)
        {
            Require(Invoke("--help").ExitCode==0,"Help did not succeed.");
            Require(Invoke("--test-mod").ExitCode==2,"Missing source was not an argument error.");
            Require(Invoke("--unknown").ExitCode==2,"Unknown command was not an argument error.");
            string source=Source(root);
            Require(Invoke("test-mod",source,"--select","","--selection",Path.Combine(root,"selection.json")).ExitCode==2,"Mutually exclusive selection inputs were accepted.");
            Require(Invoke("test-mod",source,"--character","invalid-fighter").ExitCode==2,"Unknown target character was not an argument error.");
        }
        private static void Options(string root)
        {
            string source=Source(root); var before=Fingerprint(source);
            ModCheckReport report=Report(Invoke("--list-options",source,"--json","-"),0);
            Require(report.Options.Count==2 && report.Options.Single(o=>o.Id=="").DefaultSelected && !report.Options.Single(o=>o.Id=="Extras").DefaultSelected,"Options did not expose the valid empty root ID and exact defaults.");
            Require(report.Character=="Ingrid" && !report.IntegrityVerified,"Listing implied native byte verification or guessed the wrong fighter.");
            Unchanged(source,before);
        }
        private static void Selections(string root)
        {
            string source=Source(root); var before=Fingerprint(source);
            ModCheckReport defaults=Report(Invoke("test-mod",source,"--json","-"),0);
            Require(defaults.CanInstall && defaults.IntegrityVerified && defaults.SelectedIds.SequenceEqual(new[]{""}) && defaults.FileCount==2,"Default source composition was not verified correctly.");
            Require(defaults.Files.Single(f=>f.Path==Material).Sha256==NativeModCheck.HashFile(Safety.Under(source,Material)),"Default material hash is not the actual source hash.");
            ModCheckReport selected=Report(Invoke("test-mod",source,"--select","eXtRaS","--select","","--select","EXTRAS","--character","esf032","--json","-"),0);
            Require(selected.CanInstall && selected.IntegrityVerified && selected.SelectedIds.SequenceEqual(new[]{"","Extras"}),"Explicit selections lost base-first order, deduplication or canonical IDs.");
            Require(selected.Overrides.Count==1 && selected.Files.Single(f=>f.Path==Material).ComponentId=="Extras" && selected.Files.Single(f=>f.Path==Material).Sha256==NativeModCheck.HashFile(Safety.Under(source,"Extras/"+Material)),"Explicit overlay winner was not the verified file.");
            Require(!Directory.EnumerateDirectories(root).Any(d=>Path.GetFileName(d)=="game"),"test-mod created an install tree."); Unchanged(source,before);
        }
        private static void SelectionFiles(string root)
        {
            string source=Source(root); var before=Fingerprint(source);
            string selection=Path.Combine(root,"selection.json"); Json.WriteNew(selection,new[]{"","eXtRaS"});
            ModCheckReport array=Report(Invoke("test-mod",source,"--selection",selection,"--json","-"),0);
            Require(array.SelectedIds.SequenceEqual(new[]{"","Extras"}),"Selection array did not preserve its intended components.");
            string manifest=Path.Combine(root,"selection-manifest.json"); Json.WriteNew(manifest,new InstallManifest {SourcePath=source,SelectedIds=new List<string>{"","Extras"}});
            Require(Report(Invoke("test-mod",source,"--selection",manifest,"--json","-"),0).IntegrityVerified,"Saved manifest selection did not verify.");
            string foreign=Path.Combine(root,"other-manifest.json"); Json.WriteNew(foreign,new InstallManifest {SourcePath=Path.Combine(root,"another-source"),SelectedIds=new List<string>{""}});
            Require(Report(Invoke("test-mod",source,"--selection",foreign,"--json","-"),1).Errors.Any(e=>e.Contains("different source")),"Foreign source identity was not rejected.");
            string malformed=Path.Combine(root,"null-selection.json"); File.WriteAllText(malformed,"[null]");
            Require(Report(Invoke("test-mod",source,"--selection",malformed,"--json","-"),1).Errors.Count>0,"Null selection IDs were not rejected."); Unchanged(source,before);
        }
        private static void ValidationErrors(string root)
        {
            string source=Source(root); var before=Fingerprint(source);
            ModCheckReport missing=Report(Invoke("test-mod",source,"--select","missing","--json","-"),1);
            Require(!missing.CanInstall && !missing.IntegrityVerified && missing.Errors.Any(e=>e.Contains("no longer exists")),"Missing option did not produce a composition error.");
            ModCheckReport target=Report(Invoke("test-mod",source,"--character","Juri","--json","-"),1);
            Require(!target.CanInstall && !target.IntegrityVerified && target.Errors.Any(e=>e.Contains("another character")),"Cross-character target was accepted.");
            ModCheckReport absent=Report(Invoke("test-mod",Path.Combine(root,"missing.zip"),"--json","-"),1);
            Require(!absent.CanInstall && absent.Errors.Count>0,"Missing archive did not return structured failure."); Unchanged(source,before);
        }
        private static void ReportSafety(string root)
        {
            string source=Source(root); var before=Fingerprint(source);
            string report=Path.Combine(root,"new-reports","pass.json");
            Require(Invoke("test-mod",source,"--json",report).ExitCode==0 && Json.Read<ModCheckReport>(report).IntegrityVerified,"New report was not written correctly.");
            byte[] original=File.ReadAllBytes(report);
            Require(Invoke("test-mod",source,"--json",report).ExitCode==1 && File.ReadAllBytes(report).SequenceEqual(original),"Existing report was overwritten.");
            string inside=Path.Combine(source,"new-reports","unsafe.json");
            Require(Invoke("test-mod",source,"--json",inside).ExitCode==1 && !Directory.Exists(Path.GetDirectoryName(inside)),"Report path created files inside the source.");
            string game=Path.Combine(root,"game"); Directory.CreateDirectory(game);
            string inGame=Path.Combine(game,"reports","unsafe.json");
            Require(Invoke("test-mod",source,"--game-root",game,"--json",inGame).ExitCode==1 && !Directory.Exists(Path.GetDirectoryName(inGame)),"Report path created files inside the configured game.");
            Require(!NativeModCheck.IsWithin(source+"-sibling",source) && NativeModCheck.IsWithin(Path.Combine(source,"child"),source),"Containment confused path-prefix siblings and actual descendants.");
            Unchanged(source,before);
        }
        private static void CorruptZip(string root)
        {
            string source=Source(root), archive=Path.Combine(root,"corrupt.zip");
            ZipFile.CreateFromDirectory(source,archive,CompressionLevel.NoCompression,false);
            byte[] bytes=File.ReadAllBytes(archive); bool changed=false;
            for (int offset=0;offset+46<bytes.Length;offset++)
            {
                if (BitConverter.ToUInt32(bytes,offset)!=0x02014b50) continue;
                int length=BitConverter.ToUInt16(bytes,offset+28);
                if (offset+46+length>bytes.Length || Encoding.UTF8.GetString(bytes,offset+46,length)!=Mesh) continue;
                bytes[offset+16]^=1; changed=true; break;
            }
            Require(changed,"Could not locate fixture mesh CRC in the ZIP central directory."); File.WriteAllBytes(archive,bytes);
            string fingerprint=NativeModCheck.HashFile(archive);
            Require(Report(Invoke("list-options",archive,"--json","-"),0).Options.Count==2,"Option listing unexpectedly reads every native payload.");
            ModCheckReport report=Report(Invoke("test-mod",archive,"--json","-"),1);
            Require(!report.CanInstall && !report.IntegrityVerified && report.Errors.Any(e=>e.IndexOf("checksum",StringComparison.OrdinalIgnoreCase)>=0),"Corrupt selected mesh CRC was accepted.");
            Require(NativeModCheck.HashFile(archive)==fingerprint,"Integrity check changed the corrupt archive.");
        }
        private static void NativeSandboxGuards(string root)
        {
            string source=Source(root); var before=Fingerprint(source);
            string loader=Path.Combine(root,"guard-only-loader.exe"); File.WriteAllText(loader,"This is a guard fixture and must never be launched.");
            string game=Path.Combine(root,"game"); Directory.CreateDirectory(game);
            string existing=Path.Combine(root,"existing"); Put(existing,"keep.txt","sandbox sentinel");
            ModCheckReport collision=Report(Invoke("test-loader",source,"--loader",loader,"--game-root",game,"--sandbox",existing,"--json","-"),1);
            Require(collision.Errors.Any(e=>e.Contains("NEW directory")) && File.ReadAllText(Path.Combine(existing,"keep.txt"))=="sandbox sentinel","Existing native test sandbox was not preserved.");
            foreach (string inside in new[]{Path.Combine(source,"new-sandbox"),Path.Combine(game,"new-sandbox")})
            {
                ModCheckReport overlap=Report(Invoke("test-loader",source,"--loader",loader,"--game-root",game,"--sandbox",inside,"--json","-"),1);
                Require(overlap.Errors.Any(e=>e.Contains("separate")) && !Directory.Exists(inside),"Overlapping native test sandbox was created or not explicitly rejected.");
            }
            Require(File.ReadAllText(loader)=="This is a guard fixture and must never be launched.","Sandbox guard changed the loader fixture."); Unchanged(source,before);
        }
        private static void NativePathArguments()
        {
            NativeModCheck.ValidateNativeArgument(@"C:\ASCII fixture\new-sandbox","Sandbox");
            string prefix=@"C:\fixture\";
            NativeModCheck.ValidateNativeArgument(prefix+new string('a',259-prefix.Length),"Sandbox");
            foreach (int length in new[]{260,261})
            {
                bool refused=false;
                try { NativeModCheck.ValidateNativeArgument(prefix+new string('a',length-prefix.Length),"Sandbox"); }
                catch (IOException ex) { refused=ex.Message.Contains("260"); }
                Require(refused,"Native path of "+length+" characters bypassed the fixed buffer guard.");
            }
            Encoding ansi=Encoding.GetEncoding(Encoding.Default.CodePage,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
            string incompatible=null;
            foreach (string value in new[]{"\u4e2d","\u0627","\ud83e\uddea","\u0394"})
            {
                try { if (ansi.GetString(ansi.GetBytes(value))!=value) { incompatible=value; break; } }
                catch (EncoderFallbackException) { incompatible=value; break; }
            }
            if (incompatible==null) Console.WriteLine("NOTE native code page "+Encoding.Default.CodePage+" represents every test codepoint; unsupported-codepoint subcase is not applicable.");
            else
            {
                bool refused=false;
                try { NativeModCheck.ValidateNativeArgument(prefix+incompatible+"\\sandbox","Sandbox"); }
                catch (IOException ex) { refused=ex.Message.Contains("code page"); }
                Require(refused,"Unrepresentable native path was accepted for code page "+Encoding.Default.CodePage+".");
            }
        }
    }
}
