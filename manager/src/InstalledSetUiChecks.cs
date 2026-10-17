using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static class InstalledSetUiChecks
    {
        private const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window,int index);
        internal static int Run(string outputDirectory)
        {
            string output=Path.Combine(Path.GetFullPath(outputDirectory),"ui-"+Guid.NewGuid().ToString("N"));
            Safety.NoReparseChain(output);Directory.CreateDirectory(output);
            var report=new List<string>(); bool previous=Program.Headless;Program.Headless=true;
            try
            {
                var config=new AppConfig {GameRoot=Path.Combine(output,"fixture-game"),Libraries=new List<string>()};
                string character=Path.Combine(config.CostumeRoot,"Juri");Directory.CreateDirectory(character);
                File.WriteAllText(Path.Combine(character,"A Manual archive.zip"),"opaque archive fixture");
                Directory.CreateDirectory(Path.Combine(character,"B Manual folder","natives"));
                File.WriteAllText(Path.Combine(character,"B Manual folder","natives","fixture.bin"),"manual bytes");
                File.WriteAllText(Path.Combine(character,"D Manual archive.7z"),"opaque second archive fixture");
                CreateManaged(Path.Combine(character,"C Managed outfit"),"C Managed outfit",Guid.NewGuid().ToString("N"));
                string disabledId=Guid.NewGuid().ToString("N");
                CreateManaged(Path.Combine(Path.GetDirectoryName(config.CostumeRoot),".slotsloader-installer-disabled",disabledId),"E Disabled outfit",disabledId);
                Console.WriteLine("Constructing hidden fixture window.");
                using(var form=new MainForm(config))
                {
                    Set(form,"firstLoad",false);form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                    Set(form,"installed",Installer.ListInstalled(config.CostumeRoot));Call(form,"FillInstalled");
                    Call(form,"ShowPage",Get<Panel>(form,"installedPage"));
                    var list=Get<ListView>(form,"installedList");
                    Console.WriteLine("Fixture window ready; exercising native selection.");
                    Require(list.MultiSelect && list.Items.Count==5,"Installed list did not enable native multiselect or fixture discovery.");
                    ClickRow(list,0,0);ClickRow(list,2,8);
                    Require(list.SelectedItems.Count==2,"Ctrl-click did not retain both selections.");
                    Call(form,"ShowInstalledSelection");
                    Require(Get<Label>(form,"installedTitle").Text.Contains("2"),"Aggregate title lost selection count.");
                    Require(Get<Button>(form,"removeInstalledButton").Enabled,"Mixed manual/managed removal is unavailable.");
                    Require(!Get<Button>(form,"editInstalledButton").Enabled && !Get<Button>(form,"toggleInstalledButton").Enabled,"Single-outfit action remained enabled for multiple selections.");
                    report.Add("PASS: native Ctrl-click selects separate rows, aggregate details show count, mixed manual/managed uninstall enabled, single-edit actions gated.");
                    ClickRow(list,0,0);ClickRow(list,3,4);
                    Require(list.SelectedItems.Count==4,"Shift-click range selection failed.");
                    report.Add("PASS: native Shift-click selects the contiguous range.");
                    Save(form,output,"installed-multiselect");
                    Get<Button>(form,"clearInstalledSelectionButton").PerformClick();
                    Require(list.SelectedItems.Count==0 && !Get<Button>(form,"removeInstalledButton").Enabled,"Clear left stale removal targets.");
                    // 2.5.4: Clear also puts the character/outfit/type filters back to All; with only a filter set it is enabled.
                    {
                        // 1.0.1: the character filter is the portrait picker (a set of names), the type filter a segmented toggle.
                        var stateFilter=Get<ComboBox>(form,"installedState");var kindFilter=Get<SegmentedChoice>(form,"installedKind");var clear=Get<Button>(form,"clearInstalledSelectionButton");int all=list.Items.Count;
                        Func<int> characters=delegate { return ((System.Collections.Generic.IEnumerable<string>)typeof(MainForm).GetProperty("InstalledCharacterFilter",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(form,null)).Count(); };
                        Require(!clear.Enabled,"Clear enabled with nothing to clear.");
                        Call(form,"SetInstalledCharacters",(object)new[]{"Juri"});stateFilter.SelectedItem="Enabled";kindFilter.Choose(1);Application.DoEvents();
                        Require(clear.Enabled && list.SelectedItems.Count==0,"Clear not enabled for set filters.");
                        clear.PerformClick();Application.DoEvents();
                        Require(characters()==0 && stateFilter.SelectedIndex==0 && kindFilter.SelectedIndex==0 && list.Items.Count==all && !clear.Enabled,"Clear did not reset the filters: "+characters()+"/"+stateFilter.SelectedIndex+"/"+kindFilter.SelectedIndex+", rows "+list.Items.Count+" of "+all);
                        report.Add("PASS: Clear also resets the character, outfit and type filters (2.5.4).");
                    }
                    var search=Get<PaddedTextField>(form,"installedSearch");search.Text="A Manual";Application.DoEvents();
                    Require(list.Items.Count==1,"Fixture search did not narrow the list.");
                    Get<Button>(form,"selectAllInstalledButton").PerformClick();
                    Require(list.SelectedItems.Count==1 && list.SelectedItems[0].Text=="A Manual archive.zip","Select all selected a hidden outfit.");
                    Require(Get<Button>(form,"removeInstalledButton").Visible && Get<Button>(form,"removeInstalledButton").Enabled,"Manual archive cannot be explicitly uninstalled.");
                    search.Clear();Application.DoEvents();Get<Button>(form,"selectAllInstalledButton").PerformClick();
                    Require(list.SelectedItems.Count==5,"Select all missed an outfit.");
                    Call(form,"SetBusy",true,"Fixture busy");
                    Require(!Get<Button>(form,"removeInstalledButton").Enabled && !Get<Button>(form,"presetsButton").Enabled,"Busy state permits another mutation.");
                    Call(form,"SetBusy",false,"Ready");
                    report.Add("PASS: Clear removes targets; Select all is limited to visible search results; manual archive action and busy gating work.");
                    foreach(int width in new[]{1232,1110})
                    {
                        form.MinimumSize=Size.Empty;form.ClientSize=new Size(DpiMetrics.Px(form,width),DpiMetrics.Px(form,610));Application.DoEvents();
                        foreach(string field in new[]{"selectAllInstalledButton","clearInstalledSelectionButton","presetsButton","removeInstalledButton"}) CheckFits(Get<Control>(form,field),field);
                        Require(list.Height>=DpiMetrics.Px(form,190),"Selection toolbar crowded the installed list.");
                        Require((GetWindowLong(list.Handle,-16)&0x100000)==0,"Installed list retains an unnecessary horizontal scrollbar after resize.");
                        report.Add("PASS: installed selection controls fit "+width+" logical client at "+form.DeviceDpi+" DPI.");
                    }
                    Save(form,output,"installed-minimum");form.Close();
                }
                OutfitSetInfo preset=OutfitSetStore.CreatePreset(config.CostumeRoot,"Weekend & favourites");
                Require(preset.OutfitCount==4,"Preset unexpectedly included disabled outfit.");
                using(var manager=new OutfitSetsForm(config.CostumeRoot))
                {
                    manager.ShowInTaskbar=false;manager.Opacity=0;manager.Show();WaitIdle(manager);
                    var sets=Get<ListView>(manager,"sets");Require(sets.Items.Count==1,"Preset manager did not load saved fixture.");
                    sets.Items[0].Selected=true;Application.DoEvents();
                    Require(Get<Button>(manager,"restore").Enabled && Get<Button>(manager,"delete").Enabled,"Selected preset actions are unavailable.");
                    Require(Get<TextBox>(manager,"details").Text.Contains("A Manual archive.zip"),"Preset details omit saved outfits.");
                    var name=Get<PaddedTextField>(manager,"nameInput");name.Text="Another preset";name.LayoutEditor();
                    Require(Math.Abs(name.Editor.Top-(name.Height-name.Editor.Bottom))<=1,"Preset name field is not centered.");
                    Save(manager,output,"presets");
                    manager.Size=manager.MinimumSize;Application.DoEvents();
                    foreach(string field in new[]{"save","restore","delete","refresh","close","cancel"}) {var control=Get<Control>(manager,field);if(control.Visible)CheckFits(control,field);}
                    Save(manager,output,"presets-minimum");
                    Require(sets.Height>DpiMetrics.Px(manager,100),"Preset list collapses at minimum size: size="+manager.Size+" minimum="+manager.MinimumSize+" client="+manager.ClientSize+" list="+sets.Size);
                    report.Add("PASS: preset manager loads complete named set, lists manual archive, enables selected actions, centers native name field and fits minimum geometry at "+manager.DeviceDpi+" DPI.");
                    manager.Close();
                }
                ScopedPresets(output,report);
                string explanation="Restore “"+new string('W',100)+"”? Missing outfits will be added and identical copies skipped. Different files at an existing name stop the restore. Other installed outfits stay unchanged.";
                using(var confirmation=OutfitSetsForm.CreateConfirmation("Restore saved outfits",explanation,Enumerable.Range(1,80).Select(i=>"Juri / Saved outfit "+i),"Restore outfits",false))
                {
                    confirmation.ShowInTaskbar=false;confirmation.Opacity=0;confirmation.Show();confirmation.Size=confirmation.MinimumSize;Application.DoEvents();
                    var summary=(TextBox)confirmation.Controls.Find("confirmationSummary",true).Single();
                    var names=(TextBox)confirmation.Controls.Find("confirmationOutfits",true).Single();
                    Require(summary.Text==explanation && summary.ScrollBars==ScrollBars.Vertical,"Long confirmation loses scrollable explanation.");
                    Require(names.Lines.Length==80 && names.ScrollBars==ScrollBars.Vertical && names.Height>DpiMetrics.Px(confirmation,65),"Confirmation loses full scrollable outfit list.");
                    Save(confirmation,output,"confirmation-minimum");confirmation.Close();
                    report.Add("PASS: confirmation retains full long preset name and 80 outfit names in separate scrollable areas at minimum geometry.");
                }
                report.Add("All images are hidden native-control renders of generated fixtures, not live desktop captures. No real app Data, game or libraries changed.");
                File.WriteAllLines(Path.Combine(output,"set-ui-checks.txt"),report);Console.WriteLine(String.Join(Environment.NewLine,report));Console.WriteLine("UI fixtures: "+output);return 0;
            }
            catch(Exception error) {report.Add("FAIL: "+error);File.WriteAllLines(Path.Combine(output,"set-ui-checks.txt"),report);Console.WriteLine(String.Join(Environment.NewLine,report));throw;}
            finally {Program.Headless=previous;}
        }
        // 2.4.1: one preset of each scope on a generated game (synthetic PAKs, PAK method) with extra outfits and
        // replacements; the window lists scope and counts, shows the restore choice only for a Both preset, and fits.
        private static void ScopedPresets(string output,List<string> report)
        {
            IProcessProbe previousProbe=GameProcesses.Probe;GameProcesses.Probe=new FakeProcessProbe();
            try
            {
                ReplaceChecks.Fixture f=ReplaceChecks.Create(Path.Combine(output,"scope-game"),false,ReplaceMethods.Pak);
                string costumeRoot=Path.Combine(f.Game,"reframework","costume_mods"),character=Path.Combine(costumeRoot,"Juri");
                Directory.CreateDirectory(Path.Combine(character,"Goth Juri","natives"));File.WriteAllText(Path.Combine(character,"Goth Juri","natives","fixture.bin"),"manual bytes");
                File.WriteAllText(Path.Combine(character,"Juri Summer.zip"),"opaque archive fixture");
                ReplaceChecks.Install(f,"modA","Cammy Colors");ReplaceChecks.Install(f,"modB","Cammy Alt");
                OutfitSetStore.CreatePreset(costumeRoot,"Weekend outfits",PresetScope.ExtraOutfits,f.Engine);
                OutfitSetStore.CreatePreset(costumeRoot,"Replacements only",PresetScope.Replacements,f.Engine);
                OutfitSetInfo both=OutfitSetStore.CreatePreset(costumeRoot,"Everything",PresetScope.Both,f.Engine);
                using(var manager=new OutfitSetsForm(costumeRoot,delegate {return f.Engine;},null))
                {
                    manager.ShowInTaskbar=false;manager.Opacity=0;manager.Show();WaitIdle(manager);
                    var sets=Get<ListView>(manager,"sets");Require(sets.Items.Count==3,"Scoped presets did not load.");
                    var subtitles=sets.Items.Cast<ListViewItem>().Select(i=>OutfitSetsForm.Subtitle((OutfitSetInfo)i.Tag)).ToList();
                    Require(subtitles.Any(t=>t.StartsWith("2 extra outfits · 2 replacements · ")) && subtitles.Any(t=>t.StartsWith("2 replacements · ")) && subtitles.Any(t=>t.StartsWith("2 extra outfits · ") && !t.Contains("replacement")),"Scoped subtitles: "+String.Join(" | ",subtitles));
                    var restoreScope=Get<SegmentedChoice>(manager,"restoreScope");
                    sets.Items.Cast<ListViewItem>().Single(i=>((OutfitSetInfo)i.Tag).Name=="Weekend outfits").Selected=true;Application.DoEvents();
                    Require(!restoreScope.Visible,"The restore choice shows for a preset holding one kind.");
                    foreach(ListViewItem item in sets.Items)item.Selected=false;
                    sets.Items.Cast<ListViewItem>().Single(i=>((OutfitSetInfo)i.Tag).Id==both.Id).Selected=true;Application.DoEvents();
                    Require(restoreScope.Visible && restoreScope.SelectedIndex==(int)PresetScope.Both && Get<TextBox>(manager,"details").Text.Contains("1. Cammy Alt"),"The Both preset lacks its restore choice or contents.");
                    var scope=Get<SegmentedChoice>(manager,"scope");
                    Require(scope.Options.SequenceEqual(new[]{"Extra outfits","Replacements","Both"}) && scope.LabelsFit() && restoreScope.LabelsFit(),"Scope labels are clipped.");
                    Require(Get<NoteLabel>(manager,"note").Text==OutfitSetsForm.NoteText,"Top note changed.");
                    foreach(string field in new[]{"save","restore","delete","refresh","close"}) {var b=Get<Button>(manager,field);Require(b.AccessibleName==b.Text && b.Height==DpiMetrics.Px(manager,32),field+" is not a compact button with a matching accessible name.");}
                    Get<PaddedTextField>(manager,"nameInput").Text="Friday night";
                    Save(manager,output,"presets-scopes");
                    manager.Size=manager.MinimumSize;Application.DoEvents();
                    foreach(string field in new[]{"nameInput","scope","save","restore","restoreScope","delete","refresh","close"}) CheckFits(Get<Control>(manager,field),field);
                    Require(scope.LabelsFit() && restoreScope.LabelsFit(),"Scope labels are clipped at the minimum size.");
                    Save(manager,output,"presets-scopes-minimum");
                    report.Add("PASS: one preset of each scope lists its counts (\"2 extra outfits · 2 replacements · …\"), the restore choice shows only for a Both preset, compact buttons with matching accessible names, everything fits at the minimum size at "+manager.DeviceDpi+" DPI.");
                    manager.Close();
                }
            }
            finally {GameProcesses.Probe=previousProbe;}
        }
        private static void CreateManaged(string folder,string name,string id)
        {
            Directory.CreateDirectory(Path.Combine(folder,"natives"));File.WriteAllText(Path.Combine(folder,"natives","fixture.bin"),"managed bytes");
            Json.Write(Path.Combine(folder,".slots-installer.json"),new InstallManifest {Id=id,Character="Juri",Name=name,SourcePath="fixture-source-"+id+".zip",CreatedUtc=DateTime.UtcNow.ToString("o"),SelectedIds=new List<string>{"main"},Sha256=new Dictionary<string,string>{{"natives/fixture.bin",new string('a',64)}}});
        }
        private static void ClickRow(ListView list,int index,int modifiers)
        {
            list.Items[index].EnsureVisible();Rectangle row=list.Items[index].Bounds;int point=((row.Top+row.Height/2)<<16)|(Math.Min(row.Right-5,row.Left+70)&0xffff);
            // Native ListView may enter its drag-detection message loop on down;
            // queue both messages so that loop can consume the matching release.
            PostMessage(list.Handle,0x201,new IntPtr(modifiers|1),new IntPtr(point));PostMessage(list.Handle,0x202,new IntPtr(modifiers),new IntPtr(point));Application.DoEvents();
        }
        private static void WaitIdle(Form form)
        {
            DateTime deadline=DateTime.UtcNow.AddSeconds(15);do {Application.DoEvents();Thread.Sleep(10);if(!Get<bool>(form,"busy"))return;}while(DateTime.UtcNow<deadline);
            throw new InvalidOperationException("Preset UI worker did not finish.");
        }
        private static void CheckFits(Control c,string name)
        {
            Require(c.Visible && c.Width>0 && c.Height>0,name+" is missing.");
            Control parent=c.Parent;Rectangle inside=new Rectangle(parent.Padding.Left,parent.Padding.Top,parent.ClientSize.Width-parent.Padding.Horizontal,parent.ClientSize.Height-parent.Padding.Vertical);
            Require(inside.Contains(c.Bounds),name+" is clipped: "+c.Bounds+" parent="+inside);
        }
        private static T Get<T>(object o,string name) {return (T)o.GetType().GetField(name,PrivateInstance).GetValue(o);}
        private static void Set(object o,string name,object value) {o.GetType().GetField(name,PrivateInstance).SetValue(o,value);}
        private static object Call(object o,string name,params object[] args) {return o.GetType().GetMethod(name,PrivateInstance).Invoke(o,args);}
        private static void Save(Control c,string path,string name) {using(var bitmap=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(path,name+"-"+c.DeviceDpi+".png"),ImageFormat.Png);}}
        private static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
    }
}
