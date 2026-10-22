using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    internal sealed class ModCheckReport
    {
        public string Format = "sf6-installer-check-v1";
        public string Source, Character, Scope = "Selected loose-native composition and byte integrity; not an in-game rendering test.";
        public bool CanInstall, IntegrityVerified;
        public int FileCount;
        public long TotalBytes;
        public List<string> SelectedIds = new List<string>();
        public List<string> Errors = new List<string>(), Warnings = new List<string>();
        public List<OptionSummary> Options = new List<OptionSummary>();
        public List<CheckedFile> Files = new List<CheckedFile>();
        public List<FileOverride> Overrides = new List<FileOverride>();
        public NativeCheckResult NativeLoader;
    }
    internal sealed class OptionSummary
    {
        public string Id, Name, ParentId;
        public bool Category, MainFiles, DefaultSelected;
        public int Files;
    }
    internal sealed class CheckedFile { public string Path, ComponentId, Sha256; public long Bytes; }

    // Console and GUI command dispatch share the same backend; no Form is created here.
    internal static class CommandLine
    {
        internal static bool Handles(string command)
        {
            return new[] { "--help", "-h", "help", "--list-options", "list-options", "--test-mod", "test-mod", "--test-loader", "test-loader" }.Contains(command) || ReplaceCommandLine.Commands.Contains((command ?? "").TrimStart('-'));
        }
        internal sealed class Options
        {
            internal string Command, Source, JsonFile, SelectionFile, Character, Loader, GameRoot, Sandbox;
            internal string SevenZip = "";   // 1.0.11: empty = SevenZipTool finds one
            internal List<string> Selected = new List<string>();
            internal bool HasSelection;
        }
        internal static int Run(string[] args)
        {
            if (args.Length > 0 && ReplaceCommandLine.Commands.Contains(args[0].TrimStart('-'))) return ReplaceCommandLine.Run(args);
            Options options = null;
            var report = new ModCheckReport();
            try
            {
                options = Parse(args);
                if (options.Command == "help") { Help(); return 0; }
                report.Source = options.Source;
                ValidateReportPath(options);
                using (var cancellation = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try
                    {
                        using (OperationContext.Begin(cancellation.Token, null))
                        using (var reader = new SourceReader(options.Source, options.SevenZip))
                        {
                            ModPackage package = PackageReader.Load(options.Source, reader, false);
                            var defaults = Composer.DefaultSelection(package);
                            report.Character = options.Character ?? package.Character;
                            report.Options = package.Components.Select(c => new OptionSummary { Id = c.Id, Name = c.Name, ParentId = c.ParentId, Category = c.IsDummy, MainFiles = Composer.IsBase(c), DefaultSelected = defaults.Contains(c.Id, StringComparer.OrdinalIgnoreCase), Files = c.Assets.Count }).ToList();
                            if (options.Command == "list-options") { report.Warnings.AddRange(package.Warnings); Write(report, options); return 0; }
                            var selected = ReadSelection(options, defaults);
                            CompositionPlan plan = Composer.Plan(package, selected);
                            report.SelectedIds = plan.Layers.Select(c => c.Id).ToList();
                            report.Errors.AddRange(plan.Errors); report.Warnings.AddRange(plan.Warnings);
                            report.Overrides = plan.Overrides; report.FileCount = plan.Files.Count; report.TotalBytes = plan.TotalBytes;
                            ValidateCharacter(plan, report.Character, report.Errors);
                            report.CanInstall = plan.CanInstall && report.Errors.Count == 0;
                            Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            if (report.CanInstall)
                            {
                                hashes = reader.VerifyFiles(plan.Files.Values);
                                report.IntegrityVerified = true;
                                if (options.Command == "test-loader") report.NativeLoader = NativeModCheck.Run(plan, report.Character, options.SevenZip, options.Loader, options.GameRoot, options.Sandbox);
                            }
                            report.Files = plan.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => new CheckedFile { Path = f.Key, Bytes = f.Value.Asset.Size, ComponentId = f.Value.Component.Id, Sha256 = hashes.ContainsKey(f.Key) ? hashes[f.Key] : null }).ToList();
                            if (report.NativeLoader != null && !report.NativeLoader.Accepted) { report.CanInstall=false; report.Errors.Add("The native loader did not accept every detected outfit. Inspect the retained loader report and log."); }
                        }
                    }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                Write(report, options);
                return report.Errors.Count == 0 ? 0 : 1;
            }
            catch (OperationCanceledException) { report.CanInstall=false; report.Errors.Add("Cancelled. No installed game outfit was changed."); WriteFailure(report, options); return 130; }
            catch (Exception ex) { report.CanInstall = false; report.Errors.Add(ex.Message); WriteFailure(report, options); return options == null ? 2 : 1; }
        }
        private static Options Parse(string[] args)
        {
            if (args.Length == 0) return new Options { Command = "help" };
            string command = args[0].TrimStart('-');
            if (command == "h" || command == "help") return new Options { Command = "help" };
            if (!new[] { "list-options", "test-mod", "test-loader" }.Contains(command)) throw new ArgumentException("Unknown command. Run Slotsloader CLI.exe --help.");
            if (args.Length < 2 || args[1].StartsWith("--")) throw new ArgumentException(command + " requires an archive or extracted mod folder.");
            var result = new Options { Command = command, Source = Path.GetFullPath(args[1]) };
            for (int i = 2; i < args.Length; i++)
            {
                string key = args[i];
                if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + key);
                string value = args[++i];
                switch (key)
                {
                    case "--select": result.HasSelection = true; result.Selected.Add(value); break;
                    case "--selection": result.HasSelection = true; result.SelectionFile = Path.GetFullPath(value); break;
                    case "--sevenzip": result.SevenZip = Path.GetFullPath(value); break;
                    case "--json": result.JsonFile = value == "-" ? value : Path.GetFullPath(value); break;
                    case "--character": result.Character = ResolveCharacter(value); break;
                    case "--loader": result.Loader = Path.GetFullPath(value); break;
                    case "--game-root": result.GameRoot = Path.GetFullPath(value); break;
                    case "--sandbox": result.Sandbox = Path.GetFullPath(value); break;
                    default: throw new ArgumentException("Unknown option: " + key);
                }
            }
            if (result.SelectionFile != null && result.Selected.Count > 0) throw new ArgumentException("Use either --selection or repeated --select options.");
            if (command == "test-loader" && (result.Loader == null || result.GameRoot == null || result.Sandbox == null)) throw new ArgumentException("test-loader requires --loader, --game-root and a NEW --sandbox directory.");
            return result;
        }
        private static List<string> ReadSelection(Options options, List<string> defaults)
        {
            if (options.SelectionFile == null) return options.HasSelection ? options.Selected : defaults;
            string json = File.ReadAllText(options.SelectionFile, Encoding.UTF8).TrimStart();
            List<string> selected;
            if (json.StartsWith("[")) selected = Json.Serializer().Deserialize<List<string>>(json);
            else
            {
                InstallManifest saved = Json.Serializer().Deserialize<InstallManifest>(json);
                if (saved != null && !String.IsNullOrEmpty(saved.SourcePath) && !Path.GetFullPath(saved.SourcePath).Equals(options.Source, StringComparison.OrdinalIgnoreCase)) throw new IOException("The saved selection belongs to a different source. Use a JSON array of option IDs for an intentional source change.");
                selected = saved == null ? null : saved.SelectedIds;
            }
            if (selected == null || selected.Any(id => id == null)) throw new IOException("Selection must be a JSON array of option IDs or a Slotsloader Manager outfit manifest (.slots-installer.json) with SelectedIds.");
            return selected;
        }
        private static string ResolveCharacter(string value)
        {
            string code = value.StartsWith("esf", StringComparison.OrdinalIgnoreCase) ? value.Substring(3) : value;
            string name; if (Characters.ByCode.TryGetValue(code, out name)) return name;
            name = Characters.Names.FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (name == null) throw new ArgumentException("Unknown character: " + value);
            return name;
        }
        private static void ValidateCharacter(CompositionPlan plan, string character, List<string> errors)
        {
            if (String.IsNullOrWhiteSpace(character)) { errors.Add("Character is ambiguous or unknown. Choose --character; assets are never retargeted."); return; }
            string expected = Characters.ByCode.First(p => p.Value == character).Key;
            var codes = plan.Files.Keys.SelectMany(p => System.Text.RegularExpressions.Regex.Matches(p, @"(?:/esf/esf|/esf)(\d{3})(?:/|_)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)).Distinct().ToList();
            if (codes.Any(c => c != expected)) errors.Add("Selected native files contain another character: " + String.Join(", ", codes.Select(c => "esf" + c)) + ".");
        }
        private static void ValidateReportPath(Options options)
        {
            if (options.JsonFile == null || options.JsonFile == "-") return;
            string path = options.JsonFile;
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Report already exists; choose a new --json path.");
            if (NativeModCheck.IsWithin(path, options.Source) || (options.GameRoot != null && NativeModCheck.IsWithin(path, options.GameRoot))) throw new IOException("Write reports outside the source mod and installed game.");
            Safety.NoReparseChain(path);
        }
        private static void Write(ModCheckReport report, Options options)
        {
            if (options.JsonFile == "-") { Console.WriteLine(Json.Serializer().Serialize(report)); return; }
            if (options.JsonFile != null) { Directory.CreateDirectory(Path.GetDirectoryName(options.JsonFile)); Json.WriteNew(options.JsonFile, report); }
            if (options.Command == "list-options") foreach (OptionSummary option in report.Options) Console.WriteLine("{0}  {1}  [{2}]  {3} files", option.Category ? "group" : option.DefaultSelected ? "default" : "option", option.Name, option.Id, option.Files);
            else Console.WriteLine("{0}: {1} files, {2}; integrity {3}.", report.Errors.Count == 0 ? "PASS" : "FAIL", report.FileCount, Utilities.FriendlyBytes(report.TotalBytes), report.IntegrityVerified ? "verified" : "not verified");
            foreach (string error in report.Errors) Console.WriteLine("ERROR: " + error);
            foreach (string warning in report.Warnings) Console.WriteLine("NOTE: " + warning);
            if (report.NativeLoader != null) Console.WriteLine("Native loader: {0}; {1} outfits retained, {2} rejected. Evidence: {3}", report.NativeLoader.Accepted ? "accepted" : "check failed", report.NativeLoader.Kept, report.NativeLoader.Rejected, report.NativeLoader.Sandbox);
            if (options.Command != "list-options") Console.WriteLine(report.Scope);
            if (options.JsonFile != null) Console.WriteLine("Report: " + options.JsonFile);
        }
        private static void WriteFailure(ModCheckReport report, Options options)
        {
            if (options == null) { foreach (string error in report.Errors) Console.Error.WriteLine(error); return; }
            // Never overwrite a report when validation itself rejected an existing destination.
            if (options.JsonFile != null && options.JsonFile != "-" && (File.Exists(options.JsonFile) || Directory.Exists(options.JsonFile))) options.JsonFile = null;
            try { ValidateReportPath(options); Write(report, options); }
            catch { foreach (string error in report.Errors) Console.Error.WriteLine(error); }
        }
        private static void Help()
        {
            Console.WriteLine("Slotsloader CLI (Slotsloader Manager). Works without the app window and without starting the game.");
            Console.WriteLine("  list-options <mod.zip|folder> [--json <new-report.json|->]");
            Console.WriteLine("  test-mod <source> [--select <id> ... | --selection <selection.json>] [--character <name|esfNNN>] [--json <new-report.json|->]");
            Console.WriteLine("  test-loader <source> [selection options] --loader <costume_loader.exe> --game-root <SF6 folder> --sandbox <NEW folder> [--json <new-report.json|->]");
            Console.WriteLine("Use --sevenzip <7z.exe> for RAR/7Z. Without selection options, only the normal default components are selected; incomplete bundles fail clearly.");
            Console.WriteLine("A selection JSON is an ordered array of IDs (the empty string is a valid root ID), or an installed .slots-installer.json manifest.");
            Console.WriteLine("test-mod verifies selected files and ZIP CRC without installing. test-loader copies the composition and loader metadata into a new sandbox; reads the original base PAK without writing it. DLC PAKs are omitted and reported.");
            Console.WriteLine("Exit codes: 0 passed, 1 validation failure, 2 invalid arguments, 130 cancelled. Passing does not prove rendered in-game compatibility.");
            ReplaceCommandLine.Help();
        }
    }

    internal static class CommandLineProgram
    {
        [STAThread] private static int Main(string[] args) { return CommandLine.Run(args); }
    }
}
