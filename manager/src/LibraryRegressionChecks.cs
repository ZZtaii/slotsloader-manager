using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    internal static class LibraryRegressionChecks
    {
        internal static List<string> Run(string root)
        {
            root=Path.GetFullPath(root); Directory.CreateDirectory(root);
            string previousCache=Library.IdentityCacheDirectory;
            var results=new List<string>();
            try
            {
                string cache=Path.Combine(root,"cache"); Library.IdentityCacheDirectory=cache;
                string library=Path.Combine(root,"library"); Directory.CreateDirectory(library);
                string good=Path.Combine(library,"Ryu.zip"); MakeZip(good,"001");
                string missing=Path.Combine(root,"missing-library"), notDirectory=Path.Combine(root,"not-a-directory.txt");
                File.WriteAllText(notDirectory,"fixture");
                var entries=Library.Scan(new[] {missing,library,notDirectory,library.ToUpperInvariant(),missing.ToUpperInvariant()},"");
                Require(entries.Count == 3 && entries.Count(e=>e.Detection == "Error") == 2 && entries.Single(e=>e.SourcePath == good).Character == "Ryu","unavailable library aborted healthy results or duplicated rows");
                Require(entries.Where(e=>e.Detection == "Error").All(e=>e.Character == "" && e.FilenameCharacter == "" && !String.IsNullOrWhiteSpace(e.DetectionError)),"library diagnostics falsely guessed a character");
                results.Add("PASS missing/not-directory libraries preserve healthy rows and visible diagnostics");

                entries=Library.Scan(new[] { library, (string)null },"");
                Require(entries.Count == 2 && entries.Single(e=>e.Kind == "Library").SourcePath == "","invalid configured path lacks safe diagnostic row");
                results.Add("PASS invalid configured path does not abort other libraries");

                string bad=Path.Combine(library,"Juri.zip"); File.WriteAllText(bad,"not an archive");
                entries=Library.Scan(new[] {library},"");
                LibraryEntry failed=entries.Single(e=>e.SourcePath == bad);
                Require(failed.Detection == "Error" && failed.Character == "" && failed.FilenameCharacter == "" && failed.OptionCount == -1,"corrupt source retained a guessed identity");
                LibraryIdentityCache saved=Json.Read<LibraryIdentityCache>(Path.Combine(cache,Library.CacheFilename));
                Require(saved.Records.All(e=>e.SourcePath != bad),"failed source entered persistent cache");
                MakeZip(bad,"016");
                Require(Library.Scan(new[] {library},"").Single(e=>e.SourcePath == bad).Character == "Juri","repaired source did not recover on refresh");
                results.Add("PASS corrupt sources stay uncached and recover after replacement");

                Library.ClearIdentityCache();
                using(File.Open(bad,FileMode.Open,FileAccess.Read,FileShare.None))
                {
                    entries=Library.Scan(new[] {library},"");
                    Require(entries.Single(e=>e.SourcePath == bad).Detection == "Error" && entries.Single(e=>e.SourcePath == good).Character == "Ryu","locked archive aborted its healthy sibling");
                }
                Require(Library.Scan(new[] {library},"").Single(e=>e.SourcePath == bad).Character == "Juri","unlocked source remained an error");
                results.Add("PASS locked source isolation and recovery");

                string vanished=Path.Combine(library,"Cammy vanished.zip"); MakeZip(vanished,"009");
                Library.Scan(new[] {library},"");
                File.Delete(vanished);
                failed=Library.DescribeEntry(vanished); // Simulate a path disappearing after directory enumeration.
                Require(failed.Detection == "Error" && failed.Kind == "ZIP" && failed.Character == "" && failed.FilenameCharacter == "","disappearing entry was dropped or guessed");
                Library.Scan(new[] {library},"");
                saved=Json.Read<LibraryIdentityCache>(Path.Combine(cache,Library.CacheFilename));
                Require(saved.Records.All(e=>e.SourcePath != vanished),"failed enumeration left its old cache record");
                results.Add("PASS disappearing entry produces a diagnostic and invalidates its old cache");

                using(var cancelled=new CancellationTokenSource())
                using(OperationContext.Begin(cancelled.Token,null))
                {
                    cancelled.Cancel(); bool observed=false;
                    try { Library.Scan(new[] {library},""); } catch(OperationCanceledException) { observed=true; }
                    Require(observed,"pre-cancelled scan continued");
                }
                results.Add("PASS cancellation before scanning remains cancellation");

                string parallel=Path.Combine(root,"parallel"); Directory.CreateDirectory(parallel);
                for(int i=0;i<16;i++) MakeZip(Path.Combine(parallel,"entry-"+i+".zip"),"001");
                using(var cancelled=new CancellationTokenSource())
                using(OperationContext.Begin(cancelled.Token,delegate(OperationProgress p) { if(p.Stage == "Inspecting mod libraries" && p.Completed == 1) cancelled.Cancel(); }))
                {
                    bool observed=false;
                    try { Library.Scan(new[] {parallel},""); } catch(OperationCanceledException) { observed=true; }
                    Require(observed,"parallel scan converted cancellation into source errors or completion");
                }
                results.Add("PASS parallel inspection observes captured cancellation");

                var progress=new List<long>();
                using(OperationContext.Begin(CancellationToken.None,delegate(OperationProgress p) { if(p.Stage == "Inspecting mod libraries") progress.Add(p.Completed); }))
                    entries=Library.Scan(new[] {parallel},"");
                Require(progress.Count >= 2 && progress[0] == 0 && progress.Last() == entries.Count && progress.SequenceEqual(progress.OrderBy(value=>value)),"inspection progress is incomplete or moves backward");
                results.Add("PASS parallel inspection progress is ordered and reaches total");

                // 2.0.1: costume keys from the same listing pass, cache v2 and "cammy, c1" search.
                string costumes=Path.Combine(root,"costumes"); Directory.CreateDirectory(costumes);
                MakeZipFolders(Path.Combine(costumes,"Cammy Barefoot.zip"),"009/001");
                MakeZipFolders(Path.Combine(costumes,"Cammy Boots.zip"),"009/002");
                MakeZipFolders(Path.Combine(costumes,"Cammy Hands.zip"),"009/000");
                MakeZipFolders(Path.Combine(costumes,"Cammy Mix.zip"),"009/001","009/003","009/000");
                MakeZipFolders(Path.Combine(costumes,"Cammy Style C1.zip"),"009/002");
                MakeZipFolders(Path.Combine(costumes,"Juri Boots.zip"),"016/002");
                File.WriteAllText(Path.Combine(costumes,"Cammy Notes C1.zip"),"not an archive");
                string costumeCache=Path.Combine(root,"costume-cache"); Library.IdentityCacheDirectory=costumeCache;
                Directory.CreateDirectory(costumeCache);
                File.WriteAllText(Path.Combine(costumeCache,"library-native-index-v1.json"),"{\"Format\":\"sf6-native-library-index-v1\",\"Records\":[]}");
                int misses=Library.CacheMisses;
                var listed=Library.Scan(new[] {costumes},"");
                Require(Library.CacheMisses-misses == 7,"first scan did not list every archive once: "+(Library.CacheMisses-misses));
                misses=Library.CacheMisses; Library.IdentityCacheDirectory=costumeCache;   // reload from disk
                listed=Library.Scan(new[] {costumes},"");
                Require(Library.CacheMisses-misses == 1,"second scan re-listed cached archives (only the unreadable one is retried): "+(Library.CacheMisses-misses));
                Require(Json.Read<LibraryIdentityCache>(Path.Combine(costumeCache,Library.CacheFilename)).Format == Library.CacheFormat && File.Exists(Path.Combine(costumeCache,"library-native-index-v1.json")),"cache files");
                Func<string,LibraryEntry> named=delegate(string n) { return listed.Single(e=>e.Name == n); };
                Require(named("Cammy Mix").CostumeKeys.SequenceEqual(new[] {"009/000","009/001","009/003"}) && LibrarySearch.CostumeLabel(named("Cammy Mix")) == "C1, C3 + shared" && LibrarySearch.CostumeLabel(named("Cammy Hands")) == "Shared parts" && LibrarySearch.CostumeLabel(named("Cammy Boots")) == "C2","costume keys/labels: "+String.Join(",",named("Cammy Mix").CostumeKeys));
                Func<string,List<string>> search=delegate(string text)
                {
                    LibrarySearch.Query q=LibrarySearch.Parse(text,MainForm.SearchCharacterTerm); var codes=LibrarySearch.CodesFor(q.Characters);
                    return listed.Where(e=>LibrarySearch.MatchesText(e,q,MainForm.EntryMatchesCharacter,MainForm.EntryMatchesName))
                        .Select(e=>new KeyValuePair<LibraryEntry,int>(e,LibrarySearch.CostumeRank(e,q,codes,MainForm.EntryMatchesCharacter))).Where(p=>p.Value >= 0)
                        .OrderBy(p=>p.Value).ThenBy(p=>p.Key.Name,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key.Name+"#"+p.Value).ToList();
                };
                var c1=search("cammy, c1");
                Require(c1.SequenceEqual(new[] {"Cammy Barefoot#0","Cammy Mix#0","Cammy Hands#1","Cammy Notes C1#2","Cammy Style C1#2"}),"cammy, c1: "+String.Join(", ",c1));
                Require(search("cammy, costume 1").SequenceEqual(c1) && search("Cammy, C01").SequenceEqual(c1) && search("cammy, o1").SequenceEqual(c1) && search("cammy, outfit 1").SequenceEqual(c1) && search("cammy, costume:1").SequenceEqual(c1),"costume term spellings differ");
                var c2=search("c2");
                Require(c2.SequenceEqual(new[] {"Cammy Boots#0","Cammy Style C1#0","Juri Boots#0"}),"c2 across characters: "+String.Join(", ",c2));
                Require(search("cammy, juri, c2").SequenceEqual(c2.Concat(new[] {"Cammy Hands#1"})) && search("juri, c2").SequenceEqual(new[] {"Juri Boots#0"}),"several characters");
                Require(search("cammy, c1, mix").SequenceEqual(new[] {"Cammy Mix#0"}) && search("barefoot").SequenceEqual(new[] {"Cammy Barefoot#0"}),"name words with costume terms / plain search");
                // 2.0.4: the start of a character name (three letters or more, one character only) is that character.
                Require(search("cam, c1").SequenceEqual(c1) && search("CAMM, costume 1").SequenceEqual(c1) && search("jur, c2").SequenceEqual(new[] {"Juri Boots#0"}),"character name starts: "+String.Join(", ",search("cam, c1")));
                Require(MainForm.SearchCharacterTerm("vip") == "C. Viper" && MainForm.SearchCharacterTerm("cvi") == "C. Viper" && MainForm.SearchCharacterTerm("aku") == "Akuma" && MainForm.SearchCharacterTerm("aki") == "A.K.I" && MainForm.SearchCharacterTerm("mai") == "Mai" && MainForm.SearchCharacterTerm("ma") == null && MainForm.SearchCharacterTerm("ju") == null && MainForm.SearchCharacterTerm("barefoot") == null,"character term prefixes");
                results.Add("PASS costume keys from the listing pass, cache v4 rescans once, 'cammy, c1' ranks costume files, shared parts, then title matches; 'c2' spans characters");

                // 2.0.3: tool kinds from the same listing pass (files outside natives), cache v3 and tool-mod search.
                string kinds=Path.Combine(root,"kinds"); Directory.CreateDirectory(kinds);
                string mesh="natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883";
                MakeZipFiles(Path.Combine(kinds,"Hitbox Viewer.zip"),"reframework/autorun/hitbox.lua","reframework/autorun/hitbox/util.lua");
                MakeZipFiles(Path.Combine(kinds,"Frame Meter Plugin.zip"),"Frame Meter v2/reframework/plugins/meter.dll","Frame Meter v2/readme.txt");   // wrapper folder
                MakeZipFiles(Path.Combine(kinds,"Proxy Loader.zip"),"dinput8.dll","amd_ags_x64.dll","readme.txt");                                   // game-root DLL proxies
                MakeZipFiles(Path.Combine(kinds,"Training Suite.zip"),"Suite/modinfo.ini","Suite/screenshot.png","Suite/reframework/autorun/suite.lua","Suite/reframework/plugins/suite.dll","Suite/reframework/data/suite/config.json");   // nested modinfo layout
                MakeZipFiles(Path.Combine(kinds,"Cammy Color Pack.zip"),"Main/modinfo.ini","Main/"+mesh,"Main/reframework/data/colors/cammy.json");  // costume + REFramework colour data (2.3.0: Cammy's, no tool kind): not a tool mod
                MakeZipFiles(Path.Combine(kinds,"Training Fonts.zip"),"Fonts/reframework/fonts/ui.ttf","Fonts/reframework/data/settings.json");        // REFramework files only: a tool mod
                MakeZipFiles(Path.Combine(kinds,"Cammy Mac Pack.zip"),mesh,"__MACOSX/reframework/autorun/._hitbox.lua","Thumbs.db");
                MakeZipFiles(Path.Combine(kinds,"Code Vein Jacket.zip"),mesh);
                string folderScript=Path.Combine(kinds,"Folder Script"); Directory.CreateDirectory(Path.Combine(folderScript,"reframework","autorun"));
                File.WriteAllText(Path.Combine(folderScript,"reframework","autorun","folder.lua"),"-- fixture");
                string kindCache=Path.Combine(root,"kind-cache"); Library.IdentityCacheDirectory=kindCache;
                misses=Library.CacheMisses;
                listed=Library.Scan(new[] {kinds},"");
                Require(Library.CacheMisses-misses == 9,"first tool-kind scan: "+(Library.CacheMisses-misses));
                Func<string,string> kindsOf=delegate(string n) { return String.Join("+",listed.Single(e=>e.Name == n).ToolKinds); };
                Require(kindsOf("Hitbox Viewer") == "script" && kindsOf("Frame Meter Plugin") == "plugin" && kindsOf("Proxy Loader") == "dll" && kindsOf("Training Suite") == "script+plugin+reframework"
                    && kindsOf("Cammy Color Pack") == "" && kindsOf("Training Fonts") == "reframework" && kindsOf("Cammy Mac Pack") == "" && kindsOf("Code Vein Jacket") == "" && kindsOf("Folder Script") == "script",
                    "tool kinds: "+String.Join("; ",listed.Select(e=>e.Name+"="+String.Join("+",e.ToolKinds))));
                Require(listed.Where(LibrarySearch.IsToolMod).Select(e=>e.Name).OrderBy(n=>n).SequenceEqual(new[] {"Folder Script","Frame Meter Plugin","Hitbox Viewer","Proxy Loader","Training Fonts","Training Suite"}),"tool mods");
                Func<string,string> label=delegate(string n) { return MainForm.LibraryRowLabel(listed.Single(e=>e.Name == n),null); };
                Require(label("Hitbox Viewer") == "REFramework script" && label("Frame Meter Plugin") == "REFramework plugin" && label("Proxy Loader") == "DLL" && label("Training Suite") == "Script + plugin"
                    && label("Training Fonts") == "REFramework files" && label("Cammy Color Pack") == "C1" && label("Code Vein Jacket") == "C1" && LibrarySearch.ToolLabel(new LibraryEntry { ToolKinds=new List<string> {"plugin","dll"} }) == "Plugin + DLL","row labels: "+label("Training Suite")+" / "+label("Cammy Color Pack"));
                Func<string,string> find=delegate(string text)
                {
                    LibrarySearch.Query q=LibrarySearch.Parse(text,MainForm.SearchCharacterTerm); var codes=LibrarySearch.CodesFor(q.Characters);
                    return String.Join(", ",listed.Where(e=>LibrarySearch.MatchesText(e,q,MainForm.EntryMatchesCharacter,MainForm.EntryMatchesName) && LibrarySearch.CostumeRank(e,q,codes,MainForm.EntryMatchesCharacter) >= 0 && MainForm.MatchesModType(e,q.Type))
                        .Select(e=>e.Name).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase));
                };
                string allTools="Folder Script, Frame Meter Plugin, Hitbox Viewer, Proxy Loader, Training Fonts, Training Suite";
                foreach(string text in new[] {"reframework","REFramework","tool","tools","tool mods","Tool  Mod","code mods","ref","is:tool","type:tool"}) Require(find(text) == allTools,"'"+text+"': "+find(text));
                Require(find("code") == "Code Vein Jacket, "+allTools,"'code' (tool kind OR name): "+find("code"));
                Require(find("plugin") == "Frame Meter Plugin, Training Suite" && find("plugins") == find("plugin"),"'plugin': "+find("plugin"));
                Require(find("lua") == "Folder Script, Hitbox Viewer, Training Suite" && find("scripts") == find("lua") && find("script") == find("lua"),"'lua': "+find("lua"));
                Require(find("dll") == "Frame Meter Plugin, Proxy Loader, Training Suite" && find("DLLs") == find("dll"),"'dll': "+find("dll"));
                Require(find("is:tool, suite") == "Training Suite" && find("suite, plugin") == "Training Suite" && find("is:tool color") == "" && find("color") == "Cammy Color Pack" && find("reframeworks") == "","combined terms: "+find("is:tool, suite")+" / "+find("color"));
                // 2.0.4: typing a start of the long keywords (three letters or more) already lists them.
                foreach(string text in new[] {"refr","refra","REFRAM","reframewor","refw","too","tool"}) Require(find(text) == allTools,"prefix '"+text+"': "+find(text));
                Require(find("cod") == find("code") && find("plu") == find("plugin") && find("plugi") == find("plugin") && find("scr") == find("lua") && find("scrip") == find("lua"),"prefix kinds: "+find("plu")+" / "+find("scr"));
                Require(LibrarySearch.ToolKeyword("re") == null && LibrarySearch.ToolKeyword("pl") == null && LibrarySearch.ToolKeyword("refx") == null && LibrarySearch.ToolKeyword("plugger") == null,"short or diverging terms must not be tool keywords");
                // Cached rescan; a current record without tool kinds is listed once again.
                misses=Library.CacheMisses; Library.IdentityCacheDirectory=kindCache;
                listed=Library.Scan(new[] {kinds},"");
                Require(Library.CacheMisses-misses == 1 && find("reframework") == allTools,"cached tool-kind rescan (only the folder is listed): "+(Library.CacheMisses-misses));
                string v3=Path.Combine(kindCache,Library.CacheFilename);
                LibraryIdentityCache kindRecords=Json.Read<LibraryIdentityCache>(v3);
                kindRecords.Records.Single(r=>Path.GetFileName(r.SourcePath) == "Training Suite.zip").ToolKinds=null;
                Json.Write(v3,kindRecords);
                misses=Library.CacheMisses; Library.IdentityCacheDirectory=kindCache;
                listed=Library.Scan(new[] {kinds},"");
                Require(Library.CacheMisses-misses == 2 && kindsOf("Training Suite") == "script+plugin+reframework","a record without tool kinds was not relisted once: "+(Library.CacheMisses-misses));
                // A v2 cache (2.0.1/2.0.2) is ignored and kept; every archive is listed once into the current (v4) index.
                string upgradeCache=Path.Combine(root,"kind-cache-v2"); Directory.CreateDirectory(upgradeCache);
                string v2Text="{\"Format\":\"sf6-native-library-index-v2\",\"Records\":["+String.Join(",",Json.Read<LibraryIdentityCache>(v3).Records.Select(r=>"{\"SourcePath\":"+Json.Serializer().Serialize(r.SourcePath)+",\"Stamp\":"+Json.Serializer().Serialize(r.Stamp)+",\"CharacterCodes\":"+Json.Serializer().Serialize(r.CharacterCodes)+",\"CostumeKeys\":"+Json.Serializer().Serialize(r.CostumeKeys)+",\"OptionCount\":"+r.OptionCount+",\"OptionIds\":"+Json.Serializer().Serialize(r.OptionIds)+"}"))+"]}";
                File.WriteAllText(Path.Combine(upgradeCache,"library-native-index-v2.json"),v2Text);
                misses=Library.CacheMisses; Library.IdentityCacheDirectory=upgradeCache;
                listed=Library.Scan(new[] {kinds},"");
                Require(Library.CacheMisses-misses == 9 && find("reframework") == allTools,"v2 upgrade did not list every archive once: "+(Library.CacheMisses-misses));
                Require(File.ReadAllText(Path.Combine(upgradeCache,"library-native-index-v2.json")) == v2Text && Json.Read<LibraryIdentityCache>(Path.Combine(upgradeCache,Library.CacheFilename)).Records.All(r=>r.ToolKinds != null),"v2 cache was changed or the current index lacks tool kinds");
                misses=Library.CacheMisses; Library.IdentityCacheDirectory=upgradeCache;
                Library.Scan(new[] {kinds},"");
                Require(Library.CacheMisses-misses == 1,"upgraded cache was not reused: "+(Library.CacheMisses-misses));
                results.Add("PASS tool kinds outside natives (script/plugin/dll/reframework, wrapper and modinfo folders, __MACOSX ignored); 'reframework'/'code'/'tool mods'/'ref'/is:tool list tool mods, not costume mods with REFramework data; plugin/lua/dll by kind; row labels; cache v4 reuse, missing tool kinds relisted once, v2 kept and relisted once");
                return results;
            }
            finally { Library.IdentityCacheDirectory=previousCache; }
        }
        private static void MakeZip(string path,string code)
        {
            using(var stream=new FileStream(path,FileMode.Create,FileAccess.ReadWrite))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
            {
                string entry="natives/stm/product/model/esf/esf"+code+"/001/01/fixture.mesh.230110883";
                using(var writer=new StreamWriter(zip.CreateEntry(entry).Open(),Encoding.UTF8)) writer.Write("native fixture");
            }
        }
        private static void MakeZipFolders(string path,params string[] folders)
        {
            using(var stream=new FileStream(path,FileMode.Create,FileAccess.ReadWrite))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
                foreach(string folder in folders)
                {
                    string[] p=folder.Split('/');
                    string entry="natives/stm/product/model/esf/esf"+p[0]+"/"+p[1]+"/01/esf"+p[0]+"_"+p[1]+"_01.mesh.230110883";
                    using(var writer=new StreamWriter(zip.CreateEntry(entry).Open(),Encoding.UTF8)) writer.Write("fixture "+folder);
                }
        }
        private static void MakeZipFiles(string path,params string[] entries)
        {
            using(var stream=new FileStream(path,FileMode.Create,FileAccess.ReadWrite))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
                foreach(string entry in entries)
                    using(var writer=new StreamWriter(zip.CreateEntry(entry).Open(),Encoding.UTF8)) writer.Write("fixture "+entry);
        }
        private static void Require(bool condition,string message) { if(!condition) throw new Exception("Library regression: "+message); }
    }
}
