namespace BydTools.Wwise.Ww2ogg.Exceptions;

public class SizeMismatchException : ParseException
{
    private readonly uint real_size;
    private uint read_size;
    protected override string Reason => $"expected {real_size} bits, read {read_size}";

    public SizeMismatchException(uint real_s, uint read_s)
    {
        this.real_size = real_s;
        this.read_size = read_s;
    }
}
