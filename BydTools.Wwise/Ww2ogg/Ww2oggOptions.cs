using BydTools.Wwise.Ww2ogg.Exceptions;

namespace BydTools.Wwise.Ww2ogg;

public class Ww2oggOptions
{
    public string InFilename { get; set; } = "";
    public string OutFilename { get; set; } = "";
    public string CodebooksFilename { get; set; } = "";
    public bool InlineCodebooks { get; set; }
    public bool FullSetup { get; set; }
    public ForcePacketFormat ForcePacketFormat { get; set; } =
        ForcePacketFormat.NoForcePacketFormat;

    public Ww2oggOptions()
    {
        this.InFilename = "";
        this.OutFilename = "";
        this.CodebooksFilename = "packed_codebooks.bin";
        this.InlineCodebooks = false;
        this.FullSetup = false;
        this.ForcePacketFormat = ForcePacketFormat.NoForcePacketFormat;
    }

    public void ParseArguments(int argc, string[] argv)
    {
        bool set_input = false;
        bool set_output = false;
        for (int i = 1; i < argc; i++)
        {
            if (argv[i] == "-o")
            {
                // switch for output file name
                if (i + 1 >= argc)
                {
                    throw new ArgumentError("-o needs an option");
                }

                if (set_output)
                {
                    throw new ArgumentError("only one output file at a time");
                }

                OutFilename = argv[++i];
                set_output = true;
            }
            else if (argv[i] == "--inline-codebooks")
            {
                // switch for inline codebooks
                InlineCodebooks = true;
            }
            else if (argv[i] == "--full-setup")
            {
                // early version with setup almost entirely intact
                FullSetup = true;
                InlineCodebooks = true;
            }
            else if (argv[i] == "--mod-packets" || argv[i] == "--no-mod-packets")
            {
                if (ForcePacketFormat != ForcePacketFormat.NoForcePacketFormat)
                {
                    throw new ArgumentError(
                        "only one of --mod-packets or --no-mod-packets is allowed"
                    );
                }

                if (argv[i] == "--mod-packets")
                {
                    ForcePacketFormat = ForcePacketFormat.ForceModPackets;
                }
                else
                {
                    ForcePacketFormat = ForcePacketFormat.ForceNoModPackets;
                }
            }
            else if (argv[i] == "--pcb")
            {
                // override default packed codebooks file
                if (i + 1 >= argc)
                {
                    throw new ArgumentError("--pcb needs an option");
                }

                CodebooksFilename = argv[++i];
            }
            else
            {
                // assume anything else is an input file name
                if (set_input)
                {
                    throw new ArgumentError("only one input file at a time");
                }

                InFilename = argv[i];
                set_input = true;
            }
        }

        if (!set_input)
        {
            throw new ArgumentError("input name not specified");
        }

        if (!set_output)
        {
            int found = InFilename.LastIndexOfAny((Convert.ToString('.')).ToCharArray());

            OutFilename = InFilename.Substring(0, found);
            OutFilename += ".ogg";

            // TODO: should be case insensitive for Windows
            if (OutFilename == InFilename)
            {
                OutFilename += "_conv.ogg";
            }
        }
    }
}
