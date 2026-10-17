using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // Generated mod library and costume folder for Assistant checks. Nothing
    // here reads or writes real libraries or the game folder.
    internal sealed class AssistantFixture
    {
        internal string Root, Library, GameRoot, CostumeRoot, DataDir;
        internal string CammyPack, JuriPack, CammyBoots, IngridDress;
        internal AppConfig Config;

        internal string CammyPackId { get { return AssistantWorkspace.ModId(CammyPack); } }
        internal string JuriPackId { get { return AssistantWorkspace.ModId(JuriPack); } }
        internal string CammyBootsId { get { return AssistantWorkspace.ModId(CammyBoots); } }

        internal static AssistantFixture Create(string root)
        {
            root = Path.GetFullPath(root);
            if (Directory.Exists(root)) throw new IOException("Fixture directory already exists: " + root);
            var f = new AssistantFixture { Root = root, Library = Path.Combine(root, "library"), GameRoot = Path.Combine(root, "game"), DataDir = Path.Combine(root, "data") };
            f.CostumeRoot = Path.Combine(f.GameRoot, "reframework", "costume_mods");
            Directory.CreateDirectory(f.Library); Directory.CreateDirectory(f.CostumeRoot); Directory.CreateDirectory(f.DataDir);
            string staging = Path.Combine(root, "staging");
            f.CammyPack = ColorPack(staging, f.Library, "Cammy Barefoot Pack", "009", new[] { "Black", "Crimson", "White" }, new[] { Color.FromArgb(20, 20, 24), Color.FromArgb(120, 10, 20), Color.FromArgb(235, 235, 240) }, "Crimson");
            f.JuriPack = ColorPack(staging, f.Library, "Juri Barefoot Variants", "016", new[] { "Gold", "Midnight", "Pink" }, new[] { Color.FromArgb(210, 170, 40), Color.FromArgb(15, 15, 45), Color.FromArgb(240, 130, 190) }, null);
            // Single-option package whose only option is the empty-string root ID.
            string boots = Path.Combine(staging, "Cammy Classic Boots");
            Put(boots, "modinfo.ini", "name=Cammy Classic Boots\r\ndescription=Classic outfit with boots\r\n");
            Put(boots, Mesh("009", "002"), "boots mesh"); Put(boots, Mdf("009", "002"), "boots material");
            f.CammyBoots = Path.Combine(f.Library, "Cammy Classic Boots.zip");
            ZipFile.CreateFromDirectory(boots, f.CammyBoots);
            // A folder mod for another character.
            f.IngridDress = Path.Combine(f.Library, "Ingrid Evening Dress");
            Put(f.IngridDress, "modinfo.ini", "name=Ingrid Evening Dress\r\ndescription=Long dress\r\n");
            Put(f.IngridDress, Mesh("032", "001"), "dress mesh"); Put(f.IngridDress, Mdf("032", "001"), "dress material");
            Directory.Delete(staging, true);
            f.Config = new AppConfig { GameRoot = f.GameRoot, Libraries = new List<string> { f.Library }, SevenZipPath = "" };
            // Existing state: one managed outfit and one manual folder.
            ModPackage package = PackageReader.Load(f.CammyBoots, "");
            Installer.Install(Composer.Plan(package, new[] { "" }), f.CostumeRoot, "Cammy", "Boots", "", false);
            Put(Path.Combine(f.CostumeRoot, "Juri"), "Manual Juri/readme.txt", "manual outfit");
            return f;
        }

        internal static string Mesh(string code, string body) { return "natives/stm/product/model/esf/esf" + code + "/" + body + "/01/esf" + code + "_" + body + "_01.mesh.230110883"; }
        internal static string Mdf(string code, string body) { return "natives/stm/product/model/esf/esf" + code + "/" + body + "/01/esf" + code + "_" + body + "_01.mdf2.40"; }

        private static string ColorPack(string staging, string library, string name, string code, string[] colors, Color[] tints, string largePreview)
        {
            string dir = Path.Combine(staging, name);
            Put(dir, "modinfo.ini", "name=" + name + "\r\ndescription=Barefoot outfit with several color variants.\r\ndummymod=true\r\nscreenshot=cover.png\r\n");
            Png(Path.Combine(dir, "cover.png"), 160, 90, Color.Gray);
            Put(dir, "Main Files/modinfo.ini", "name=Main Files\r\ndescription=Required barefoot base mesh\r\n");
            Put(dir, "Main Files/" + Mesh(code, "001"), "base mesh " + name);
            Put(dir, "Main Files/" + Mdf(code, "001"), "base material " + name);
            Put(dir, "Color/modinfo.ini", "name=Color\r\ndescription=Select one color\r\ndummymod=true\r\n");
            for (int i = 0; i < colors.Length; i++)
            {
                string c = "Color/" + colors[i] + "/";
                Put(dir, c + "modinfo.ini", "name=" + colors[i] + "\r\ndescription=" + colors[i] + " wraps and nails\r\nscreenshot=screenshot.png\r\n");
                bool large = colors[i] == largePreview;
                Png(Path.Combine(dir, (c + "screenshot.png").Replace('/', Path.DirectorySeparatorChar)), large ? 2000 : 120, large ? 1000 : 80, tints[i]);
                Put(dir, c + Mdf(code, "001"), colors[i] + " material");
                Put(dir, c + Mdf(code, "001").Replace(".mdf2.40", "_ALBD.tex.241106027"), colors[i] + " texture");
            }
            string zip = Path.Combine(library, name + ".zip");
            ZipFile.CreateFromDirectory(dir, zip);
            return zip;
        }

        internal static void Put(string root, string relative, string text)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        private static void Png(string path, int width, int height, Color color)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var bitmap = new Bitmap(width, height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(color);
                    using (var pen = new Pen(Color.FromArgb(128, 255, 255, 255), Math.Max(1, width / 60))) g.DrawEllipse(pen, width / 4, height / 6, width / 2, height * 2 / 3);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}
