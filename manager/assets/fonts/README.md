# Linux interface fonts

Unmodified static Inter 4.1 fonts from the official release:
https://github.com/rsms/inter/releases/download/v4.1/Inter-4.1.zip

Release archive SHA-256:
`9883fdd4a49d4fb66bd8177ba6625ef9a64aa45899767dde3d36aa425756b11e`

Files are `extras/ttf/Inter-{Regular,Bold,Italic,BoldItalic}.ttf` in that archive.
`OFL-Inter.txt` is the archive's original `LICENSE.txt`. Do not replace these with
variable fonts: the WinForms GDI and GDI+ renderers both need the static faces.

The build copies these files beside the executables. Linux registers them privately
for the running process; it never installs fonts into the host desktop. Windows
continues to use Segoe UI. The Linux installer verifies the font files along with
the rest of the package, and the runtime probe checks GDI family and symbol coverage.

Code and fallback portrait labels use unmodified static Liberation Mono 2.1.5
Regular, Bold, Italic and Bold Italic from the official release:
https://github.com/liberationfonts/liberation-fonts/releases/tag/2.1.5

Release asset: `liberation-fonts-ttf-2.1.5.tar.gz`, SHA-256
`7191c669bf38899f73a2094ed00f7b800553364f90e2637010a69c0e268f25d0`.
`OFL-Liberation.txt` is the archive's original `LICENSE`. Windows retains Consolas.
