using BydTools.Utils;

namespace BydTools.VFS.PostProcessors;

/// <summary>
/// Creates and registers <see cref="IPostProcessor"/> instances
/// for each supported <see cref="EVFSBlockType"/>.
/// </summary>
public static class PostProcessorFactory
{
    public static Dictionary<EVFSBlockType, IPostProcessor> CreateProcessors(ILogger logger)
    {
        var processors = new Dictionary<EVFSBlockType, IPostProcessor>
        {
            { EVFSBlockType.Table, new SparkBufferPostProcessor(logger) },
            { EVFSBlockType.Video, new VideoPostProcessor(logger) },
            { EVFSBlockType.InitAudio, new PckPostProcessor(logger) },
            { EVFSBlockType.Audio, new PckPostProcessor(logger) },
            { EVFSBlockType.AudioChinese, new PckPostProcessor(logger) },
            { EVFSBlockType.AudioEnglish, new PckPostProcessor(logger) },
            { EVFSBlockType.AudioJapanese, new PckPostProcessor(logger) },
            { EVFSBlockType.AudioKorean, new PckPostProcessor(logger) },
        };

        var luaProcessor = LuaPostProcessor.TryCreate(logger);
        if (luaProcessor != null)
            processors[EVFSBlockType.Lua] = luaProcessor;

        return processors;
    }
}
