using System;
using System.Drawing;
using System.IO;

namespace SlotsInstaller
{
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
