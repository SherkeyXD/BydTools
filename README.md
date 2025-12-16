# BydTools

A collection of tools

## Usage

### VFS Command

Dump specific types of files from the game's VFS.

```auto
Usage:
  BydTools.CLI.exe vfs --gamepath <game_path> [--blocktype <type>] [--output <output_dir>] [-h|--help]

Arguments:
  --gamepath   Game data directory that contains the VFS folder
  --blocktype  Block type to dump, supports name or numeric value, default is all
               Available types: InitialAudio, InitialBundle, BundleManifest, InitialExtendData, Audio, Bundle, DynamicStreaming, Table, Video, IV, Streaming, JsonData, Lua, IFixPatch, ExtendData, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean
  --output     Output directory, default is ./Assets next to the executable
  -h, --help   Show help information

Examples:
  BydTools.CLI vfs --gamepath /path/to/game --blocktype Bundle --output /path/to/output
  BydTools.CLI vfs --gamepath /path/to/game --blocktype 12
  BydTools.CLI vfs --gamepath /path/to/game
```

### PCK Command

Extract files from PCK archives and convert audio files.

```auto
Usage:
  BydTools.CLI.exe pck --input <pck_file> --output <output_dir> [--format <format>] [--codebooks <codebooks_path>] [-h|--help]

Arguments:
  --input, -i      Input PCK file path
  --output, -o     Output directory
  --format, -f     Output format: wem, ogg, or mp3 (default: wem)
  --codebooks, -c  Path to packed_codebooks.bin (required for WEM to OGG conversion)
  -h, --help       Show help information

Examples:
  BydTools.CLI pck --input /path/to/file.pck --output /path/to/output --format ogg
  BydTools.CLI pck --input /path/to/file.pck --output /path/to/output --format mp3
  BydTools.CLI pck --input /path/to/file.pck --output /path/to/output --format wem
```

## Acknowledgements

- [isHarryh](https://github.com/isHarryh) for unpacking strategy and pck processing
- [rfi/BydTools](https://git.crepe.moe/rfi/BydTools) the original repo
- [AssetRipper/BnkExtractor](https://github.com/AssetRipper/BnkExtractor) for bnk and wem processing
- [Xiph.Org Foundation](https://www.xiph.org/) for ogg processing
