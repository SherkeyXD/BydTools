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
        };

        var luaProcessor = LuaPostProcessor.TryCreate(logger);
        if (luaProcessor != null)
            processors[EVFSBlockType.Lua] = luaProcessor;

        return processors;
    }
}
