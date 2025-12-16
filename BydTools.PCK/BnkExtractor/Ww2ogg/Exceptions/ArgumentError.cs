using System;

namespace BnkExtractor.Ww2ogg.Exceptions;

public class ArgumentError : Exception
{
    private string errmsg = "";
    public override string Message => $"Argument error: {errmsg}";

    public ArgumentError(string str)
    {
        this.errmsg = str;
    }
}
