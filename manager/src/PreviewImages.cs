using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;

namespace SlotsInstaller
{
    // UI-thread owned. Displayed images are separate copies, so an eviction or
    // package change cannot dispose an image still used by a PictureBox/viewer.
    internal sealed class PreviewImageCache : IDisposable
    {
        private sealed class Entry
        {
            internal Image Image;
            internal long Size;
            internal LinkedListNode<string> Node;
        }
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> order = new LinkedList<string>();
        private readonly long limit;
        internal long Bytes { get; private set; }
        internal int Count { get { return entries.Count; } }
        internal PreviewImageCache(long byteLimit) { limit = byteLimit; }
        internal bool TryCopy(string id, out Image copy)
        {
            copy = null; Entry entry;
            if (!entries.TryGetValue(id, out entry)) return false;
            order.Remove(entry.Node); order.AddLast(entry.Node);
            if (entry.Image != null) copy = new Bitmap(entry.Image);
            return true;
        }
        internal void Add(string id, Image image)
        {
            if (entries.ContainsKey(id)) return;
            long size = image == null ? 0 : (long)image.Width * image.Height * 4;
            if (size > limit) return;
            while (entries.Count > 0 && (Bytes + size > limit || entries.Count >= 128))
            {
                string oldest = order.First.Value; Entry old = entries[oldest];
                order.RemoveFirst(); entries.Remove(oldest); Bytes -= old.Size;
                if (old.Image != null) old.Image.Dispose();
            }
            // Full resolution is retained for the enlarged preview too. Account
            // for decoded pixels, not just the much smaller compressed file.
            Image stored = image == null ? null : new Bitmap(image);
            entries.Add(id, new Entry { Image = stored, Size = size, Node = order.AddLast(id) }); Bytes += size;
        }
        public void Dispose()
        {
            foreach (Entry entry in entries.Values) if (entry.Image != null) entry.Image.Dispose();
            entries.Clear(); order.Clear(); Bytes = 0;
        }
    }

    internal sealed class PreviewImageResult : IDisposable
    {
        internal byte[] Bytes;
        internal Image Image;
        internal static PreviewImageResult Decode(byte[] bytes)
        {
            var result=new PreviewImageResult {Bytes=bytes};
            if(bytes==null || bytes.Length==0) return result;
            try
            {
                using(var stream=new MemoryStream(bytes))
                using(var source=System.Drawing.Image.FromStream(stream))
                {
                    // Encoded size is not a bound on decoded memory. Inspect
                    // dimensions before allocating the detached 32-bit bitmap.
                    if((long)source.Width*source.Height>16L*1024*1024) return result;
                    result.Image=new Bitmap(source);
                }
            }
            catch(ArgumentException) {}
            catch(OutOfMemoryException) {}
            return result;
        }
        internal Image TakeImage() {Image image=Image;Image=null;return image;}
        public void Dispose() {if(Image!=null) {Image.Dispose();Image=null;}}
    }
}
