using System.Drawing;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static class AppBranding
    {
        internal static void Apply(Form form)
        {
            WindowChrome.Apply(form);
            using(var stream=typeof(AppBranding).Assembly.GetManifestResourceStream("SlotsInstaller.AppIcon"))
            {
                // Standalone source-test harnesses may omit the icon resource.
                if(stream==null) return;
                using(var source=new Icon(stream))
                {
                    var icon=(Icon)source.Clone();
                    form.Icon=icon;
                    form.Disposed+=delegate {icon.Dispose();};
                }
            }
        }
    }
}
