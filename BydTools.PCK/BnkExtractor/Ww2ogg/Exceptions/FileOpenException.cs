using System;

namespace BnkExtractor.Ww2ogg.Exceptions;

public class FileOpenException : Exception
{
    private string filename = "";

    public FileOpenException(string name)
    {
        this.filename = name;
    }

    public override string Message =>
        string.IsNullOrEmpty(filename) ? "Error opening a file" : $"Error opening {filename}";
}
