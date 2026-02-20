# Third-Party Notices

This project incorporates or is derived from the following third-party works.
Each component retains its original license and copyright.

---

## AnimeStudio

- **Source**: https://github.com/Escartem/AnimeStudio
- **License**: MIT (full text below)
- **Copyright**: (c) 2016 Radu; (c) 2016-2020 Perfare; (c) 2022-2024 Razmoth; (c) 2024-2025 Escartem
- **Usage**: VFS block structure parsing and asset extraction logic in `BydTools.VFS`
  is inspired by AnimeStudio's Unity asset handling approach.

### MIT License Text

```
MIT License

Copyright (c) 2016 Radu
Copyright (c) 2016-2020 Perfare
Copyright (c) 2022-2024 Razmoth
Copyright (c) 2024-2025 Escartem

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## AnimeWwise

- **Source**: https://github.com/Escartem/AnimeWwise
- **License**: CC BY-NC-SA 4.0
- **Copyright**: Escartem and contributors
- **Usage**: `PckParser.cs` references the AKPK sector-based parsing approach
  from `wavescan.py`; `PckMapper.cs` is a C# port of `mapper.py`.

---

## ww2ogg

- **Source**: https://github.com/hcs64/ww2ogg
- **License**: BSD-3-Clause (full text below)
- **Copyright**: (c) 2002 Xiph.org Foundation; (c) 2009-2016 Adam Gashlin
- **Usage**: The `BnkExtractor/Ww2ogg/` directory contains a C# port of
  the original C++ ww2ogg converter.

### BSD-3-Clause License Text

```
Copyright (c) 2002, Xiph.org Foundation
Copyright (c) 2009-2016, Adam Gashlin

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
  notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
  notice, this list of conditions and the following disclaimer in the
  documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## ReVorb

- **Source**: https://github.com/ItsBranK/ReVorb
- **Original Author**: Yirkha (HydrogenAudio forums)
- **License**: No explicit license provided in the repository
- **Copyright**: ItsBranK (Visual Studio port); Yirkha (original implementation)
- **Usage**: `BnkExtractor/Revorb/RevorbSharp.cs` is a C# port that
  recomputes OGG Vorbis granule positions.

---

## OggVorbisSharp (NuGet)

- **Source**: https://www.nuget.org/packages/OggVorbisSharp
- **Usage**: Native OGG/Vorbis bindings used by the Revorb module.

---

## libogg

- **Source**: https://github.com/xiph/ogg
- **License**: BSD-3-Clause (full text below)
- **Copyright**: (c) 2002 Xiph.org Foundation
- **Usage**: `ogg.dll` bundled in `BydTools.PCK/3rdParty/`.

### libogg BSD-3-Clause License Text

```
Copyright (c) 2002, Xiph.org Foundation

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
  notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
  notice, this list of conditions and the following disclaimer in the
  documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## libvorbis

- **Source**: https://github.com/xiph/vorbis
- **License**: BSD-3-Clause (full text below)
- **Copyright**: (c) 2002-2020 Xiph.org Foundation
- **Usage**: `vorbis.dll`, `vorbisfile.dll`, `vorbisenc.dll` bundled in
  `BydTools.PCK/3rdParty/`.

### libvorbis BSD-3-Clause License Text

```
Copyright (c) 2002-2020 Xiph.org Foundation

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
  notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
  notice, this list of conditions and the following disclaimer in the
  documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
