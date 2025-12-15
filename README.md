## BeyondTools.VFS

Dump specific types of files from the game's VFS.

```
Usage:
  BeyondTools.VFS.exe --gamepath <game_path> [--blocktype <type>] [--output <output_dir>] [-h|--help] [-v|--version]

Arguments:
  --gamepath   Game data directory that contains the VFS folder.
  --blocktype  Block type to dump, supports name or numeric value, default is all.
               Available types: InitialAudio, InitialBundle, BundleManifest, InitialExtendData, Audio, Bundle, DynamicStreaming, Table, Video, IV, Streaming, JsonData, Lua, IFixPatch, ExtendData, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean
  --output     Output directory, default is ./Assets next to the executable.
  -h, --help   Show help information.
  -v, --version   Show version information.

Examples:
  BeyondTools.VFS.exe --gamepath "D:\\Game" --blocktype Bundle --output "D:\\DumpedAssets"
  BeyondTools.VFS.exe --gamepath "D:\\Game" --blocktype 12
  BeyondTools.VFS.exe --gamepath "D:\\Game"
```


## Acknowledgements

+ [isHarryh](https://github.com/isHarryh) for unpacking strategy
+ [rfi/BeyondTools](https://git.crepe.moe/rfi/BeyondTools) the original repo