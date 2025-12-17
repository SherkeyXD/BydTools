using BydTools.PCK;
using BydTools.VFS;

namespace BydTools.CLI;

/// <summary>
/// Provides logging functionality with support for verbose mode.
/// Implements both VFS and PCK logger interfaces.
/// </summary>
public class Logger : BydTools.VFS.ILogger, BydTools.PCK.ILogger
{
    private readonly bool _verbose;

    /// <summary>
    /// Initializes a new instance of the Logger class.
    /// </summary>
    /// <param name="verbose">Whether verbose logging is enabled.</param>
    public Logger(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Writes a message to standard output (always shown).
    /// </summary>
    public void Info(string message)
    {
        Console.WriteLine(message);
    }

    /// <summary>
    /// Writes a formatted message to standard output (always shown).
    /// </summary>
    public void Info(string format, params object[] args)
    {
        Console.WriteLine(format, args);
    }

    /// <summary>
    /// Writes a message to standard output (always shown, no newline).
    /// </summary>
    public void InfoNoNewline(string message)
    {
        Console.Write(message);
    }

    /// <summary>
    /// Writes a verbose message to standard output (only shown if verbose is enabled).
    /// </summary>
    public void Verbose(string message)
    {
        if (_verbose)
        {
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Writes a formatted verbose message to standard output (only shown if verbose is enabled).
    /// </summary>
    public void Verbose(string format, params object[] args)
    {
        if (_verbose)
        {
            Console.WriteLine(format, args);
        }
    }

    /// <summary>
    /// Writes a verbose message to standard output (only shown if verbose is enabled, no newline).
    /// </summary>
    public void VerboseNoNewline(string message)
    {
        if (_verbose)
        {
            Console.Write(message);
        }
    }

    /// <summary>
    /// Writes an error message to standard error (always shown).
    /// </summary>
    public void Error(string message)
    {
        Console.Error.WriteLine(message);
    }

    /// <summary>
    /// Writes a formatted error message to standard error (always shown).
    /// </summary>
    public void Error(string format, params object[] args)
    {
        Console.Error.WriteLine(format, args);
    }
}
