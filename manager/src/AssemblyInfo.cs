using System.Runtime.Versioning;
using System.Reflection;

// The command-line compiler otherwise omits the target-framework attribute.
// WinForms uses it when enabling its .NET 4.7+ per-monitor DPI improvements.
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]
[assembly: AssemblyInformationalVersion("1.0.3")]
// 2.1.0: the app was named SlotsLoader Manager (previously SF6 SlotsLoader Installer); 2.5.9: Slotsloader Manager.
// build.ps1 defines CLI for SlotsLoader CLI.exe, which is built from the same sources.
#if CLI
[assembly: AssemblyTitle("Slotsloader CLI")]
[assembly: AssemblyDescription("Slotsloader Manager command line: checks mods and manages replacements without the app window")]
#else
[assembly: AssemblyTitle("Slotsloader Manager")]
[assembly: AssemblyDescription("Slotsloader Manager: installs and manages Street Fighter 6 outfit mods (extra Slotsloader outfits and replacements of the original files)")]
#endif
[assembly: AssemblyProduct("Slotsloader Manager")]
