using System.IO;
using BnkExtractor.Ww2ogg.Exceptions;

namespace BnkExtractor.Ww2ogg;

public static class Ww2oggConverter
{
    public static void PrintUsage()
    {
        // logging removed
    }

    internal static void Main(int argc, string[] args)
    {
        Ww2oggOptions opt = new Ww2oggOptions();

        try
        {
            opt.ParseArguments(argc, args);
        }
        catch (ArgumentError)
        {
            PrintUsage();
            return;
        }
        Main(opt);
    }

    internal static void Main(Ww2oggOptions opt)
    {
        Wwise_RIFF_Vorbis ww = new Wwise_RIFF_Vorbis(
            opt.InFilename,
            opt.CodebooksFilename,
            opt.InlineCodebooks,
            opt.FullSetup,
            opt.ForcePacketFormat
        );

        ww.PrintInfo();

        using (BinaryWriter of = new BinaryWriter(File.Create(opt.OutFilename)))
        {
            if (of == null)
            {
                throw new FileOpenException(opt.OutFilename);
            }

            ww.GenerateOgg(of);
        }
    }
}
