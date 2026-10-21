# Third-party notices

Slotsloader Manager's own code is under the personal-use license in `LICENSE`. The parts below are not; each keeps its own license.

## Code adapted from Slotsloader (MIT)

`src/ModFixes.cs` ports mod-file fixes (colour-file signatures, texture versions, mip tables) from Slotsloader's `patch.cpp` and `loader_core.cpp`, which build on [SF6 Slots Loader](https://github.com/Wael3rd/SF6-SlotsLoader) by Wael3rd. Those parts are used under the MIT License:

> Copyright (c) 2026 Wael
> Copyright (c) 2026 ZZtaii
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## bcdec (MIT)

The texture decoder used for the fighter portraits (`src/TexDecode.cs`) is a C# port of [bcdec](https://github.com/iOrange/bcdec) v0.985, used under the MIT License:

> Copyright (c) 2022 Sergii Kudlai
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Octicons (MIT)

The GitHub icon on the About cards (`src/BrandGlyphs.cs`) is the `mark-github` path from [Octicons](https://github.com/primer/octicons), used under the MIT License:

> Copyright (c) 2023 GitHub Inc.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## 7-Zip (GNU LGPL, unRAR license restriction, BSD)

Release packages include `7-Zip\7z.exe` and `7-Zip\7z.dll` from [7-Zip](https://www.7-zip.org) by Igor Pavlov, unmodified, so RAR and 7Z archives can be read on a PC without 7-Zip installed. The Manager runs `7z.exe` as a separate program and does not link to it. 7-Zip's own license file, `7-Zip\License.txt`, ships beside them and applies to those files:

> 7-Zip Copyright (C) 1999-2026 Igor Pavlov.
>
> - 7z.dll: the GNU LGPL as main license for most of the code; the GNU LGPL with the "unRAR license restriction" for some code; the BSD 3-clause and BSD 2-clause licenses for some code.
> - All other files: the GNU LGPL.

The unRAR code may be used only to extract RAR archives, not to re-create the RAR compression algorithm. The 7-Zip source code is available at [www.7-zip.org](https://www.7-zip.org). The version and SHA-256 of the bundled files are listed in each release's `VALIDATION.txt`.

## Brand marks

The Discord, Nexus Mods and Patreon logos on the About cards and in the status bar are trademarks of their owners. They are used only to link to those services. ColorSync's mark is ZZtai's own.

## At run time

Fighter portraits and stage names are read from your own installed game and cached in `Data\portraits`; they are not part of this app. The optional Assistant sends your requests to Anthropic's API with your own key.
