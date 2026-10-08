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

## vgmstream

- **Source**: https://github.com/vgmstream/vgmstream
- **Pinned commit**: `7dc938fa2f210943b37c7b6511852b516ef432ab` (`scripts/vgmstream.rev`), API 1.1.0
- **License**: ISC / MIT (depending on component)
- **Copyright**: vgmstream contributors
- **Usage**: `libvgmstream` is loaded via P/Invoke for WEM → WAV decoding in `BydTools.Audio`.
  Falls back to `vgmstream-cli` when the library is unavailable.
- **CI** builds the shared library from that commit and publishes it with the CLI. The binaries are not stored in this repository.
  - Windows ships `libvgmstream.dll` plus the x64 runtimes from vgmstream `ext_libs/dll-x64`: `libvorbis.dll`, `avcodec-vgmstream-59.dll`, `avformat-vgmstream-59.dll`, `avutil-vgmstream-57.dll`.
  - Linux and macOS link Vorbis and a trimmed static FFmpeg (`n7.1.1`, the tag vgmstream's build scripts pin) into `libvgmstream.so` / `libvgmstream.dylib`.

---

## CSChaCha20

- **Source**: https://github.com/KaarloR/CSChaCha20
- **License**: ISC
- **Copyright**: (c) 2015, 2018 Scott Bennett; (c) 2018-2023 Kaarlo Räihä
- **Usage**: ChaCha20 decryption of BLC payloads and encrypted VFS file entries, in `BydTools.Core`.

---

## XXTEA

- **Source**: https://github.com/xxtea/xxtea-dotnet
- **License**: MIT
- **Copyright**: (c) 2008-2016 Ma Bingyao. Algorithm by David J. Wheeler and Roger M. Needham.
- **Usage**: Lua script decryption in `BydTools.Formats`.
