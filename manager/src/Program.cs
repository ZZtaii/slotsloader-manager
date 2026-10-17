using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static class Program
    {
        internal static bool Headless;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && CommandLine.Handles(args[0])) { Headless = true; return CommandLine.Run(args); }
                // --dialog-check runs the app exactly like a user launch (not headless) and checks themed dialogs.
                bool dialogCheck = args.Length > 0 && args[0] == "--dialog-check";
                Headless = args.Length > 0 && args[0].StartsWith("--") && args[0] != "--open" && !dialogCheck;
                // 1.0.1: the checks never use the real Recycle Bin (uninstall recycles source-less mods); --delete-check has its own stub.
                if (Headless && args[0] != "--delete-check") RecycleBin.Current = new CheckRecycleBin();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { ShowError(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { Log(e.ExceptionObject as Exception); };
                if (args.Length > 0 && args[0] == "--self-test")
                {
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--self-test requires a workspace test directory.");
                    return IntegrationTests.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--assistant-check")
                {
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--assistant-check requires a fixture output directory.");
                    return AssistantChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--assistant-ui-check")
                {
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--assistant-ui-check requires a fixture output directory.");
                    return AssistantUiChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--assistant-live-check")
                {
                    // Opt-in only: sends real requests with ANTHROPIC_API_KEY from this process's environment.
                    Headless = true;
                    if (args.Length < 3) throw new ArgumentException("--assistant-live-check requires a fixture output directory and a step (connect or full).");
                    return AssistantLiveCheck.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
                }
                if (args.Length > 0 && args[0] == "--replace-check")
                {
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--replace-check requires a fixture output directory.");
                    return ReplaceChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--make-replace-fixture")
                {
                    // Screenshots: a generated game (synthetic PAKs), fixture mods and a fixture Fluffy instance in a NEW folder.
                    Headless = true;
                    if (args.Length < 2 || Directory.Exists(args[1])) throw new ArgumentException("--make-replace-fixture requires a new directory.");
                    ReplaceChecks.Fixture fixture = ReplaceChecks.Create(Path.GetFullPath(args[1]), false, ReplaceMethods.Pak);
                    string fluffy = Path.Combine(Path.GetFullPath(args[1]), "Fluffy", "Games", "SF6"); Directory.CreateDirectory(Path.Combine(fluffy, "Mods"));
                    File.WriteAllText(Path.Combine(fluffy, "installed.ini"), "[Fluffy Tool]\r\nModName=Fluffy Tool\r\ninstalltype=invalidate\r\nfile=reframework/autorun/fluffy_tool.lua\r\n");
                    Console.WriteLine(fixture.Game); Console.WriteLine(fixture.Mods); Console.WriteLine(Path.Combine(fluffy, "Mods"));
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--replace-ui-check")
                {
                    if (args.Length < 2) throw new ArgumentException("--replace-ui-check requires a fixture output directory.");
                    return ReplaceUiChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--replace-real-check")
                {
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--replace-real-check requires an output directory.");
                    return ReplaceRealChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--self-test-extra")
                {
                    if (args.Length < 2) throw new ArgumentException("--self-test-extra requires a fixture output directory.");
                    return ExtraRegressionChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--set-check")
                {
                    if (args.Length < 2) throw new ArgumentException("--set-check requires a fixture output directory.");
                    return OutfitSetChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--set-ui-check")
                {
                    if (args.Length < 2) throw new ArgumentException("--set-ui-check requires a fixture output directory.");
                    return InstalledSetUiChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--smoke-ui")
                {
                    Headless = true;
                    using (MainForm form = new MainForm(AppConfig.Load()))
                    {
                        form.CreateControl();
                        using (Bitmap bitmap = new Bitmap(1460, 920)) form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    }
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--exercise-ui")
                {
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--exercise-ui requires the Goth Juri archive path.");
                    return MainForm.ExerciseUI(args[1]);
                }
                if (args.Length > 0 && args[0] == "--layout-check")
                {
                    Headless = true;
                    return LayoutChecks.Run(args.Length > 1 ? args[1] : null);
                }
                if (args.Length > 0 && args[0] == "--polish-check")
                {
                    if (args.Length < 2) throw new ArgumentException("--polish-check requires a fixture output directory.");
                    return PolishChecks.Run(args[1], args.Length > 2 ? args[2] : null);
                }
                if (args.Length > 0 && args[0] == "--filter-check")
                {
                    // 2.3.0 Filters panel: decoders, portraits, categories, filters and the panel in hidden windows.
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--filter-check requires a fixture output directory.");
                    return FilterChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--updates-check")
                {
                    // 2.4.0 update check, toasts, loader update in place and Manager self-update (loopback server, temp app copy).
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--updates-check requires a new output directory.");
                    return UpdateChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--loader-check")
                {
                    // 2.3.1 pinned SlotsLoader fetch against a loopback server (no real network).
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--loader-check requires a new output directory.");
                    return LoaderChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--loader-real-check")
                {
                    // Read-only on the real libraries; one anonymous GET of the pinned URL is recorded.
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--loader-real-check requires an output directory.");
                    return LoaderChecks.RunReal(args[1], AppDomain.CurrentDomain.BaseDirectory);
                }
                if (args.Length > 0 && args[0] == "--filter-real-check")
                {
                    // Read-only: real game portraits/stage names and the real libraries' categories.
                    Headless = true;
                    if (args.Length < 3) throw new ArgumentException("--filter-real-check requires an output directory and the game folder.");
                    return FilterRealChecks.Run(args[1], args[2]);
                }
                if (args.Length > 0 && args[0] == "--delete-check")
                {
                    // 2.4.1 library Delete on generated fixtures with a stub Recycle Bin (hidden window).
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--delete-check requires a fixture output directory.");
                    return LibraryDeleteChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--wording-check")
                {
                    // 2.5.1: no user-visible text names another product (literals, CLI, Assistant, window).
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--wording-check requires a fixture output directory.");
                    return WordingChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--slots-check")
                {
                    // 2.5.0 several variants of one mod as extra slots: suggested names, hint, Reset options, Installed grouping (hidden window).
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--slots-check requires a fixture output directory.");
                    return SlotChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--variants-check")
                {
                    // 2.5.0 variant on/off for dropped archives: choices file, listing, CLI, Assistant and the Installed page (hidden window).
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--variants-check requires a fixture output directory.");
                    return VariantChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--history-check")
                {
                    // 2.4.1 install/usage history on generated fixtures (hidden windows).
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--history-check requires a fixture output directory.");
                    return HistoryChecks.Run(args[1]);
                }
                if (args.Length > 0 && args[0] == "--history-real-check")
                {
                    // Read-only: the user's SlotsLoader log (nothing is written to the game folder).
                    Headless = true;
                    if (args.Length < 3) throw new ArgumentException("--history-real-check requires an output directory and the game folder.");
                    return HistoryChecks.RunReal(args[1], args[2]);
                }
                if (args.Length > 0 && args[0] == "--field-check")
                {
                    if (args.Length < 2) throw new ArgumentException("--field-check requires a fixture output directory.");
                    return TextFieldChecks.Run(args[1]);
                }
                MainForm main = new MainForm(AppConfig.Load());
                if (dialogCheck) { DialogCheck.Attach(main, args.Length > 1 ? args[1] : null); Application.Run(main); return DialogCheck.ExitCode; }
                if (args.Length > 1 && args[0] == "--open") main.ConfigureOpeningSource(args[1]);
                if (args.Length > 0 && (args[0] == "--screenshot" || args[0] == "--screenshot-available"))
                {
                    Headless = true;
                    if (args.Length < 2) throw new ArgumentException("--screenshot requires an output PNG path.");
                    main.ConfigureScreenshot(args[1], args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null, args[0] == "--screenshot-available");
                }
                Application.Run(main);
                return Environment.ExitCode;
            }
            catch (Exception e)
            {
                ShowError(e);
                return 1;
            }
        }

        internal static void Log(Exception exception)
        {
            if (exception == null) return;
            try
            {
                Directory.CreateDirectory(AppConfig.DataDir);
                File.AppendAllText(Path.Combine(AppConfig.DataDir, "log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        // One diagnostic line in Data\log.txt (2.2.0: dialog metrics).
        internal static void LogLine(string text)
        {
            try
            {
                Directory.CreateDirectory(AppConfig.DataDir);
                File.AppendAllText(Path.Combine(AppConfig.DataDir, "log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine);
            }
            catch { }
        }

        internal static void ShowError(Exception exception)
        {
            Log(exception);
            Environment.ExitCode = 1;
            if (!Headless) ThemedMessageBox.Show(exception.Message, "Slotsloader Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
