namespace BnkExtractor.Ww2ogg.Exceptions;

public class InvalidIdException : ParseException
{
    private readonly int id;
    protected override string Reason => $"invalid codebook id {id}, try --inline-codebooks";

    public InvalidIdException(int i)
    {
        this.id = i;
    }
}
