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

## vgmstream (runtime dependency)

- **Source**: https://github.com/vgmstream/vgmstream
- **License**: ISC / MIT (depending on component)
- **Usage**: Used as an external tool (`vgmstream-cli`) for WEM → WAV conversion.
  Not bundled with this project; must be placed alongside the executable or in PATH.
