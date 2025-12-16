namespace BnkExtractor.Ww2ogg.Exceptions;

public class ParseException : Exception
{
    private string str = "";
    protected virtual string Reason => str;

    public sealed override string Message => $"Parse error: {Reason}";

    public ParseException()
        : this("unspecified.") { }

    public ParseException(string s)
    {
        this.str = s;
    }
}
