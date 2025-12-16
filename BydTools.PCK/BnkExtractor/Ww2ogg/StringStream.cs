using System.Text;

namespace BnkExtractor.Ww2ogg;

internal class StringStream
{
    public StringStream() { }

    public readonly StringBuilder stringBuilder = new StringBuilder();

    public StringStream Add(string text)
    {
        stringBuilder.Append(text);
        return this;
    }

    public StringStream Add(object? obj) => Add(obj?.ToString() ?? string.Empty);

    public string str() => stringBuilder.ToString();
}
