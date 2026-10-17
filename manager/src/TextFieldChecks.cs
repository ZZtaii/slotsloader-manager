using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static class TextFieldChecks
    {
        private const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,string text);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int length);
        internal static int Run(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var results=new List<string>();bool previousHeadless=Program.Headless;Program.Headless=true;
            try
            {
                using(var form=new MainForm(new AppConfig {GameRoot=Path.Combine(outputDirectory,"empty-game"),Libraries=new List<string>()}))
                {
                    Field("firstLoad").SetValue(form,false);form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                    results.Add("Hidden native WinForms rendering; DeviceDpi="+form.DeviceDpi+". No live desktop capture or game writes.");
                    foreach(string name in new[]{"navLibrary","navInstalled","navSettings"}) {var button=Get<Control>(form,name);Require(button.Parent.ClientRectangle.Contains(button.Bounds),name+" text/active underline is clipped by its header.");}
                    results.Add("PASS: navigation buttons and active underline fit the header interior.");
                    foreach(string name in new[]{"packageTitle","componentTitle","packageDescription","targetPath","installedTitle","installedDetail","settingsStatus"})Require(!Get<Label>(form,name).UseMnemonic,name+" interprets data ampersands as mnemonics.");
                    Show(form,"libraryPage");
                    CheckField(form,"search",results);CheckField(form,"outfitName",results);
                    var search=Get<PaddedTextField>(form,"search");
                    Require(search.PlaceholderText=="Search or is:bundle" && search.TrailingSpace>=26,"Search placeholder/trailing clear space missing.");
                    var clear=Get<Label>(form,"searchClear");search.Text="Juri";Application.DoEvents();
                    Require(clear.Visible && search.Editor.Right<clear.Left,"Search edit text overlaps clear action.");
                    Require(clear.Size==new Size(DpiMetrics.Px(search,26),DpiMetrics.Px(search,24)),"Search clear action retained unscaled geometry.");
                    var character=Get<DarkCombo>(form,"targetCharacter");character.SelectedItem="Juri";
                    Get<PaddedTextField>(form,"outfitName").Text="My Juri outfit";
                    CheckAligned(Get<PaddedTextField>(form,"outfitName"),character,"Character and outfit name",results);
                    Save(form,outputDirectory,"library-fields");
                    Save(search,outputDirectory,"search-typed");
                    search.Clear();Save(search,outputDirectory,"search-placeholder");
                    CheckNativeEditing(search,results);
                    Show(form,"installedPage");
                    CheckField(form,"installedSearch",results);
                    {
                        // 2.4.1: the Installed search has the same × (shared component), Esc clears it too.
                        var installedSearch=Get<PaddedTextField>(form,"installedSearch");var installedClear=Get<Label>(form,"installedSearchClear");
                        Require(installedClear!=null && installedClear.AccessibleName=="Clear search" && !installedClear.Visible,"Installed search has no hidden Clear search action.");
                        installedSearch.Text="aki";Application.DoEvents();
                        Require(installedClear.Visible && installedSearch.Editor.Right<installedClear.Left && installedClear.Size==new Size(DpiMetrics.Px(installedSearch,26),DpiMetrics.Px(installedSearch,24)),"Installed search clear action missing, overlapping or unscaled.");
                        Save(installedSearch,outputDirectory,"installed-search-typed");
                        typeof(Label).GetMethod("OnClick",PrivateInstance).Invoke(installedClear,new object[]{EventArgs.Empty});Application.DoEvents();
                        Require(installedSearch.TextLength==0 && !installedClear.Visible,"× did not clear the Installed search.");
                        installedSearch.Text="aki";installedSearch.Focus();Application.DoEvents();
                        Require((bool)typeof(MainForm).GetMethod("RunShortcut",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(form,new object[]{ShortcutAction.ClearSearch}) && installedSearch.TextLength==0,"Esc did not clear the Installed search.");
                        // The library's matching: "aki" finds A.K.I's extra outfit (by character) and replacement (by the
                        // costume folder its files touch) though neither name says "aki"; "cam" (unique start) finds Cammy.
                        var outfits=new List<InstalledOutfit>{
                            new InstalledOutfit{Name="Purple Coat",Character="A.K.I",Folder=@"x:\a",Managed=true,Enabled=true,SourcePath=""},
                            new InstalledOutfit{Name="Delta Red",Character="Cammy",Folder=@"x:\c",Managed=true,Enabled=true,SourcePath=""}};
                        var replaced=new ReplaceMod{Id="r1",Name="Barefoot with Legwear",SourcePath=@"x:\mods\barefoot.zip",Enabled=true};
                        replaced.Files.Add(new ModFile{Path="natives/stm/product/model/esf/esf013/003/01/esf013_003_01.mesh.230110883"});
                        typeof(MainForm).GetField("installed",PrivateInstance).SetValue(form,outfits);
                        typeof(MainForm).GetField("gameRows",PrivateInstance).SetValue(form,new List<GameModRow>{new GameModRow{Kind="Replacement",Mod=replaced,Priority=1}});
                        Func<string,string> visible=delegate(string query){installedSearch.Text=query;Application.DoEvents();typeof(MainForm).GetMethod("FillInstalled",PrivateInstance).Invoke(form,null);return String.Join(",",Get<ListView>(form,"installedList").Items.Cast<ListViewItem>().Select(i=>i.Text).OrderBy(n=>n));};
                        string aki=visible("aki"),cam=visible("cam"),word=visible("legwear"),costume=visible("aki, c3"),none=visible("zzz");
                        Require(aki=="Barefoot with Legwear,Purple Coat" && cam=="Delta Red" && word=="Barefoot with Legwear" && costume=="Barefoot with Legwear" && none=="","Installed search matching: aki '"+aki+"', cam '"+cam+"', legwear '"+word+"', aki c3 '"+costume+"', zzz '"+none+"'");
                        installedSearch.Clear();typeof(MainForm).GetMethod("FillInstalled",PrivateInstance).Invoke(form,null);
                        Require(Get<ListView>(form,"installedList").Items.Count==3,"clearing the search did not restore the list");
                        typeof(MainForm).GetField("installed",PrivateInstance).SetValue(form,new List<InstalledOutfit>());typeof(MainForm).GetField("gameRows",PrivateInstance).SetValue(form,new List<GameModRow>());
                        typeof(MainForm).GetMethod("FillInstalled",PrivateInstance).Invoke(form,null);
                        results.Add("PASS: Installed search matches like the library: aki = A.K.I (outfit by character, replacement by its costume folder), cam = Cammy, name words, aki, c3; clearing restores every row.");                        results.Add("PASS: Installed search has the library's × (Clear search; hidden while empty, clears, scaled, text stops before it) and Esc clears it.");
                    }
                    CheckAligned(Get<PaddedTextField>(form,"installedSearch"),Get<PickerButton>(form,"installedCharacterButton"),"Installed search and character",results);   // 1.0.1: the portrait picker button
                    CheckAligned(Get<PickerButton>(form,"installedCharacterButton"),Get<DarkCombo>(form,"installedState"),"Installed filters",results);
                    Save(form,outputDirectory,"installed-fields");
                    Show(form,"settingsPage");
                    typeof(MainForm).GetMethod("LoadSettingsFields",PrivateInstance).Invoke(form,null);
                    Get<PaddedTextField>(form,"settingsGame").Text=@"E:\Steam\steamapps\common\Street Fighter 6";
                    Get<PaddedTextField>(form,"settingsSevenZip").Text=@"C:\Program Files\7-Zip\7z.exe";
                    Get<ListBox>(form,"settingsLibraries").Items.Add(@"E:\Fluffy\Games\SF6\Mods");
                    CheckField(form,"settingsGame",results);CheckField(form,"settingsSevenZip",results);
                    var layout=(TableLayoutPanel)Get<PaddedTextField>(form,"settingsGame").Parent;
                    CheckAligned(Get<PaddedTextField>(form,"settingsGame"),layout.GetControlFromPosition(1,1),"Game path and Browse",results);
                    CheckAligned(Get<PaddedTextField>(form,"settingsSevenZip"),layout.GetControlFromPosition(1,5),"7-Zip path and Browse",results);
                    Save(form,outputDirectory,"settings-fields");
                    CheckDialogs(form,outputDirectory,results);
                    form.Close();
                }
                File.WriteAllLines(Path.Combine(outputDirectory,"text-field-checks.txt"),results);
                return 0;
            }
            catch(Exception error) {results.Add("FAIL: "+error);File.WriteAllLines(Path.Combine(outputDirectory,"text-field-checks.txt"),results);throw;}
            finally {Program.Headless=previousHeadless;}
        }
        private static void CheckField(MainForm form,string name,List<string> results)
        {
            var field=Get<PaddedTextField>(form,name);field.LayoutEditor();
            Require(field.Visible,name+" is not visible.");
            Require(field.Editor.Left==DpiMetrics.Px(field,7),name+" has inconsistent left padding.");
            Require(Math.Abs(field.Editor.Top-(field.Height-field.Editor.Bottom))<=1,name+" line is not vertically centered.");
            Require(field.Editor.Height>=(int)Math.Floor(field.Font.GetHeight(field.DeviceDpi)),name+" clips the native font line.");
            Require(field.Editor.Top>=DpiMetrics.Px(field,4),name+" has crowded vertical padding.");
            Require(!field.Editor.Multiline && !field.Editor.WordWrap && field.Editor.TabStop,name+" changed single-line native editing.");
            results.Add("PASS: "+name+" field="+field.Size+" editor="+field.Editor.Bounds+" font="+field.Font.SizeInPoints+"pt @ "+field.DeviceDpi+" DPI; centered native line and 7px logical inset.");
        }
        private static void CheckDialogs(MainForm owner,string outputDirectory,List<string> results)
        {
            var plan=new CompositionPlan();plan.Errors.Add("Choose an option in Skin options.");plan.Warnings.Add("This is a synthetic layout fixture; no mod files were read or written.");
            plan.Layers.Add(new ModComponent {Name="Main & shared files"});Field("plan").SetValue(owner,plan);
            using(var report=(Form)typeof(MainForm).GetMethod("CreateCompositionReport",PrivateInstance).Invoke(owner,null))
            {
                report.Opacity=0;report.Show();Application.DoEvents();
                Require(report.ClientSize.Width>=DpiMetrics.Px(report,750) && report.ClientSize.Height>=DpiMetrics.Px(report,580),"Composition dialog lost logical design size at native DPI: "+report.ClientSize);
                Require(report.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Label>().All(label=>!label.UseMnemonic),"Report layer/path data interprets ampersands as mnemonics.");
                Save(report,outputDirectory,"composition-report");
                results.Add("PASS: composition report client="+report.ClientSize+" at "+report.DeviceDpi+" DPI; scaled before rendering.");report.Close();
            }
            using(var image=new Bitmap(400,400))
            using(var viewer=(Form)typeof(MainForm).GetMethod("CreatePreviewForm",PrivateInstance).Invoke(owner,new object[]{image}))
            {
                viewer.Opacity=0;viewer.Show();Application.DoEvents();
                Require(viewer.ClientSize.Width>=DpiMetrics.Px(viewer,950),"Preview dialog lost logical design width: "+viewer.ClientSize);
                Require(Screen.FromControl(viewer).WorkingArea.Contains(viewer.Bounds),"Preview dialog exceeds available monitor bounds.");
                results.Add("PASS: image preview client="+viewer.ClientSize+" at "+viewer.DeviceDpi+" DPI; fits available monitor.");viewer.Close();
            }
        }
        private static void CheckAligned(Control first,Control second,string description,List<string> results)
        {
            Require(first.Top==second.Top && Math.Abs(first.Height-second.Height)<=1,description+" field edges differ: "+first.Bounds+" / "+second.Bounds);
            results.Add("PASS: "+description+" share top="+first.Top+" and height="+first.Height+".");
        }
        private static void CheckNativeEditing(PaddedTextField field,List<string> results)
        {
            var className=new StringBuilder(256);GetClassName(field.Editor.Handle,className,className.Capacity);
            Require(className.ToString().IndexOf("EDIT",StringComparison.OrdinalIgnoreCase)>=0,"Field lost its native EDIT window.");
            field.Text="left right";field.Editor.Select(5,5);
            int changes=0;EventHandler changed=delegate {changes++;};field.TextChanged+=changed;
            try
            {
                SendMessage(field.Editor.Handle,0xC2,new IntPtr(1),"name");
                Require(field.Text=="left name" && changes==1,"Native replacement did not preserve selection/TextChanged semantics.");
                SendMessage(field.Editor.Handle,0x304,IntPtr.Zero,IntPtr.Zero);
                Require(field.Text=="left right","Native undo did not restore selected text.");
                field.Editor.Select(field.TextLength,0);SendMessage(field.Editor.Handle,0x102,new IntPtr('!'),IntPtr.Zero);
                Require(field.Text=="left right!","Native character input failed.");
            }
            finally {field.TextChanged-=changed;field.Clear();}
            results.Add("PASS: native EDIT HWND, selection replacement, one forwarded TextChanged event, native undo and character input; native context menu/IME/clipboard handling retained (system clipboard untouched).");
        }
        private static FieldInfo Field(string name) {return typeof(MainForm).GetField(name,PrivateInstance);}
        private static T Get<T>(MainForm form,string name) {return (T)Field(name).GetValue(form);}
        private static void Show(MainForm form,string page) {typeof(MainForm).GetMethod("ShowPage",PrivateInstance).Invoke(form,new object[]{Get<Panel>(form,page)});Application.DoEvents();}
        private static void Save(Control control,string directory,string name) {using(var image=new Bitmap(control.Width,control.Height)) {control.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(directory,name+"-"+control.DeviceDpi+".png"),ImageFormat.Png);}}
        private static void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException("Text field regression: "+message);}
    }
}
