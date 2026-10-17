using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // Generated fixtures only. Run with a new directory beneath the existing test root.
    internal static class ArchiveRegressionChecks
    {
        private sealed class Item
        {
            internal string Name;
            internal byte[] Bytes, Actual, Encoded;
            internal bool Deflate, Descriptor, DescriptorNoSignature, Utf8;
            internal uint? DeclaredSize;
            internal uint Offset;
        }
        internal static List<string> Run(string root)
        {
            return Run(root,@"C:\Program Files\7-Zip\7z.exe");
        }
        internal static List<string> Run(string root,string sevenZip)
        {
            Directory.CreateDirectory(root);
            var results=new List<string>();
            Require(Crc(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926,"CRC-32 standard check vector differs");
            results.Add("PASS CRC-32 standard check vector");
            byte[] payload=Encoding.UTF8.GetBytes("A valid native fixture. "+new string('x',2048));
            foreach (string kind in new[] { "stored","deflated","descriptor","descriptor-no-signature","utf8","zip64","zip64-descriptor" })
            {
                string name=kind == "utf8" ? "衣装/texture.tex" : "natives/texture.tex";
                string path=Path.Combine(root,kind+".zip");
                CreateZip(path,new[] {new Item { Name=name,Bytes=payload,Deflate=kind != "stored",Descriptor=kind.Contains("descriptor"),DescriptorNoSignature=kind == "descriptor-no-signature",Utf8=kind == "utf8" }},kind.StartsWith("zip64",StringComparison.Ordinal));
                using (var reader=new SourceReader(path,""))
                {
                    Require(reader.ReadBytes(name,4096).SequenceEqual(payload),kind+" read differs");
                    var file=Plan(reader,name);
                    string hash=reader.VerifyFiles(new[] {file})[file.Asset.DestinationPath];
                    using (var sha=SHA256.Create()) Require(hash == BitConverter.ToString(sha.ComputeHash(payload)).Replace("-","").ToLowerInvariant(),kind+" verified hash differs");
                }
                results.Add("PASS valid ZIP "+kind+" reads and verifies without 7-Zip");
            }
            string multi=Path.Combine(root,"multiple-comments.zip");
            CreateZip(multi,new[] { new Item {Name="folder/",Bytes=new byte[0]},new Item {Name="folder/a",Bytes=payload},new Item {Name="folder/b",Bytes=new byte[0]} },false);
            using(var reader=new SourceReader(multi,"")) Require(reader.ReadBytes("folder/a",4096).SequenceEqual(payload) && reader.ReadBytes("folder/b",1).Length == 0,"directory/zero-length entry shifts checksum mapping");
            results.Add("PASS directory, empty-entry and archive-comment checksum mapping");

            string crc=Path.Combine(root,"bad-crc.zip");
            CreateZip(crc,new[] {new Item {Name="natives/mesh.mesh",Bytes=Encoding.UTF8.GetBytes("expected"),Actual=Encoding.UTF8.GetBytes("corrupt!")}},false);
            using(var reader=new SourceReader(crc,""))
            {
                Reject(delegate { reader.ReadBytes("natives/mesh.mesh",4096); },"checksum");
                var file=Plan(reader,"natives/mesh.mesh");
                Reject(delegate { reader.VerifyFiles(new[] {file}); },"checksum");
                string output=Path.Combine(root,"crc-copy"); Directory.CreateDirectory(output);
                Reject(delegate { reader.CopyFiles(new[] {file},output,root); },"checksum");
            }
            results.Add("PASS same-length ZIP corruption rejected by read, verify and copy");

            string bomb=Path.Combine(root,"bad-size.zip");
            CreateZip(bomb,new[] {new Item {Name="modinfo.ini",Bytes=Encoding.UTF8.GetBytes(new string('a',8*1024*1024)),DeclaredSize=8,Deflate=true}},false);
            using(var reader=new SourceReader(bomb,""))
            {
                Reject(delegate { reader.ReadBytes("modinfo.ini",1024*1024); },"declared size");
                string output=Path.Combine(root,"size-copy"); Directory.CreateDirectory(output);
                var file=Plan(reader,"modinfo.ini");
                Reject(delegate { reader.CopyFiles(new[] {file},output,root); },"declared size");
                string partial=Path.Combine(output,file.Asset.DestinationPath.Replace('/',Path.DirectorySeparatorChar));
                Require(new FileInfo(partial).Length <= 8,"oversize native bytes reached disk");
            }
            results.Add("PASS understated ZIP sizes stop metadata expansion and native disk writes");

            string folder=Path.Combine(root,"growing-folder"); Directory.CreateDirectory(folder);
            string metadata=Path.Combine(folder,"modinfo.ini"); File.WriteAllText(metadata,"small");
            using(var reader=new SourceReader(folder,""))
            {
                File.WriteAllText(metadata,new string('z',4096));
                Reject(delegate { reader.ReadBytes("modinfo.ini",1024); },"declared size");
            }
            results.Add("PASS changed folder metadata cannot bypass its bounded read");

            string truncated=Path.Combine(root,"truncated.zip");
            File.WriteAllBytes(truncated,File.ReadAllBytes(multi).Take(80).ToArray());
            Reject(delegate { using(var reader=new SourceReader(truncated,"")) { } },null);
            results.Add("PASS truncated ZIP directory rejected");

            using(var cancellation=new CancellationTokenSource())
            using(OperationContext.Begin(cancellation.Token,null))
            using(var reader=new SourceReader(Path.Combine(root,"stored.zip"),""))
            {
                cancellation.Cancel();
                bool cancelled=false;
                try { reader.VerifyFiles(new[] {Plan(reader,"natives/texture.tex")}); } catch(OperationCanceledException) { cancelled=true; }
                Require(cancelled,"cancelled verification continued");
            }
            results.Add("PASS verification observes operation cancellation");
            if(File.Exists(sevenZip))
            {
                string sevenSource=Path.Combine(root,"seven-source"); Directory.CreateDirectory(sevenSource);
                File.WriteAllBytes(Path.Combine(sevenSource,"first.bin"),payload);
                File.WriteAllText(Path.Combine(sevenSource,"second.bin"),"unselected fixture");
                string sevenArchive=Path.Combine(root,"selected.7z");
                ProcessUtil.RunText(sevenZip,"a -t7z -y "+ProcessUtil.Quote(sevenArchive)+" "+ProcessUtil.Quote(Path.Combine(sevenSource,"*")));
                using(var reader=new SourceReader(sevenArchive,sevenZip))
                {
                    var selected=Plan(reader,"first.bin");
                    var hashes=reader.VerifyFiles(new[] {selected});
                    using(var sha=SHA256.Create()) Require(hashes.Count == 1 && hashes[selected.Asset.DestinationPath] == BitConverter.ToString(sha.ComputeHash(payload)).Replace("-","").ToLowerInvariant(),"selected 7Z verification differs");
                }
                results.Add("PASS selected 7Z payload verification produces expected hash");
            }
            return results;
        }
        private static PlannedFile Plan(SourceReader reader,string entry)
        {
            return new PlannedFile { Asset=new NativeAsset {EntryPath=entry,DestinationPath="natives/"+Path.GetFileName(entry),Size=reader.Entries[entry].Size} };
        }
        private static void Require(bool condition,string message) { if(!condition) throw new Exception("Archive regression: "+message); }
        private static void Reject(Action action,string message)
        {
            try { action(); }
            catch(IOException ex) { Require(message == null || ex.Message.IndexOf(message,StringComparison.OrdinalIgnoreCase) >= 0,"unexpected failure: "+ex.Message); return; }
            catch(InvalidDataException ex) { Require(message == null,"unexpected ZIP failure: "+ex.Message); return; }
            throw new Exception("Archive regression: invalid source was accepted");
        }
        private static uint Crc(byte[] bytes) { var crc=new ZipCrc32(); crc.Update(bytes,bytes.Length); return crc.Value; }
        private static void CreateZip(string path,IEnumerable<Item> input,bool zip64)
        {
            Item[] items=input.ToArray();
            using(var stream=File.Create(path)) using(var writer=new BinaryWriter(stream))
            {
                foreach(Item item in items)
                {
                    item.Encoded=item.Actual ?? item.Bytes;
                    if(item.Deflate) using(var memory=new MemoryStream())
                    {
                        using(var deflate=new DeflateStream(memory,CompressionLevel.Optimal,true)) deflate.Write(item.Encoded,0,item.Encoded.Length);
                        item.Encoded=memory.ToArray();
                    }
                    byte[] name=Encoding.UTF8.GetBytes(item.Name); item.Offset=(uint)stream.Position;
                    writer.Write(0x04034b50u); writer.Write((ushort)(zip64?45:20)); writer.Write((ushort)((item.Descriptor?8:0)|(item.Utf8?2048:0)));
                    writer.Write((ushort)(item.Deflate?8:0)); writer.Write((ushort)0); writer.Write((ushort)33);
                    writer.Write(item.Descriptor ? 0 : Crc(item.Bytes));
                    writer.Write(zip64?UInt32.MaxValue:item.Descriptor?0:(uint)item.Encoded.Length);
                    writer.Write(zip64?UInt32.MaxValue:item.Descriptor?0:item.DeclaredSize ?? (uint)item.Bytes.Length);
                    writer.Write((ushort)name.Length); writer.Write((ushort)(zip64?20:0)); writer.Write(name);
                    if(zip64) { writer.Write((ushort)1); writer.Write((ushort)16); writer.Write((ulong)item.Bytes.Length); writer.Write((ulong)item.Encoded.Length); }
                    writer.Write(item.Encoded);
                    if(item.Descriptor)
                    {
                        if(!item.DescriptorNoSignature) writer.Write(0x08074b50u);
                        writer.Write(Crc(item.Bytes));
                        if(zip64) { writer.Write((ulong)item.Encoded.Length); writer.Write((ulong)item.Bytes.Length); }
                        else { writer.Write((uint)item.Encoded.Length); writer.Write((uint)item.Bytes.Length); }
                    }
                }
                long central=stream.Position;
                foreach(Item item in items)
                {
                    byte[] name=Encoding.UTF8.GetBytes(item.Name);
                    writer.Write(0x02014b50u); writer.Write((ushort)(zip64?45:20)); writer.Write((ushort)(zip64?45:20));
                    writer.Write((ushort)((item.Descriptor?8:0)|(item.Utf8?2048:0))); writer.Write((ushort)(item.Deflate?8:0));
                    writer.Write((ushort)0); writer.Write((ushort)33); writer.Write(Crc(item.Bytes));
                    writer.Write(zip64?UInt32.MaxValue:(uint)item.Encoded.Length); writer.Write(zip64?UInt32.MaxValue:item.DeclaredSize ?? (uint)item.Bytes.Length);
                    writer.Write((ushort)name.Length); writer.Write((ushort)(zip64?28:0)); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
                    writer.Write(0u); writer.Write(zip64?UInt32.MaxValue:item.Offset); writer.Write(name);
                    if(zip64) { writer.Write((ushort)1); writer.Write((ushort)24); writer.Write((ulong)item.Bytes.Length); writer.Write((ulong)item.Encoded.Length); writer.Write((ulong)item.Offset); }
                }
                long centralSize=stream.Position-central;
                if(zip64)
                {
                    long end64=stream.Position;
                    writer.Write(0x06064b50u); writer.Write(44ul); writer.Write((ushort)45); writer.Write((ushort)45); writer.Write(0u); writer.Write(0u);
                    writer.Write((ulong)items.Length); writer.Write((ulong)items.Length); writer.Write((ulong)centralSize); writer.Write((ulong)central);
                    writer.Write(0x07064b50u); writer.Write(0u); writer.Write((ulong)end64); writer.Write(1u);
                }
                byte[] comment=Encoding.UTF8.GetBytes("Generated ZIP compatibility fixture with an archive comment");
                writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write((ushort)(zip64?65535:items.Length)); writer.Write((ushort)(zip64?65535:items.Length));
                writer.Write(zip64?UInt32.MaxValue:(uint)centralSize); writer.Write(zip64?UInt32.MaxValue:(uint)central);
                writer.Write((ushort)comment.Length); writer.Write(comment);
            }
        }
    }
}
