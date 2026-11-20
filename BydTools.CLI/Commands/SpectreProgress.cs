using BydTools.Extraction;
using Spectre.Console;

namespace BydTools.CLI.Commands;

internal sealed class SpectreProgress(ProgressTask task) : IProgressSink
{
    private readonly Lock _lock = new();

    public void AddTotal(long amount)
    {
        lock (_lock)
            task.MaxValue += amount;
    }

    public void Advance(long amount)
    {
        lock (_lock)
            task.Increment(amount);
    }
}
