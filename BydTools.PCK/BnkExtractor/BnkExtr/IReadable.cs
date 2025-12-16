using System.IO;

namespace BnkExtractor.BnkExtr
{
    public interface IReadable
    {
        void Read(BinaryReader reader);

        int GetByteSize();
    }
}
