using BydTools.Wwise.Ww2ogg.Exceptions;
using BydTools.Wwise.Ww2ogg.Extensions;

namespace BydTools.Wwise.Ww2ogg;

public class Wwise_RIFF_Vorbis
{
    private string _file_name = "";
    private string _codebooks_name = "";
    private BinaryReader _infile;
    private int _file_size = -1;

    private bool _little_endian = true;

    private int _riff_size = -1;
    private int _fmt_offset = -1;
    private int _cue_offset = -1;
    private int _LIST_offset = -1;
    private int _smpl_offset = -1;
    private int _vorb_offset = -1;
    private int _data_offset = -1;
    private int _fmt_size = -1;
    private int _cue_size = -1;
    private int _LIST_size = -1;
    private int _smpl_size = -1;
    private int _vorb_size = -1;
    private int _data_size = -1;

    // RIFF fmt
    private ushort _channels = 0;
    private uint _sample_rate = 0;
    private uint _avg_bytes_per_second = 0;

    // RIFF extended fmt
    private ushort _ext_unk = 0;
    private uint _subtype = 0;

    // cue info
    private uint _cue_count = 0;

    // smpl info
    private uint _loop_count = 0;
    private uint _loop_start = 0;
    private uint _loop_end = 0;

    // vorbis info
    private uint _sample_count = 0;
    private uint _setup_packet_offset = 0;
    private uint _first_audio_packet_offset = 0;
    private uint _uid = 0;
    private byte _blocksize_0_pow = 0;
    private byte _blocksize_1_pow = 0;

    private readonly bool _inline_codebooks;
    private bool _full_setup;
    private bool _header_triad_present = false;
    private bool _old_packet_headers = false;
    private bool _no_granule = false;
    private bool _mod_packets = false;

    private Func<BinaryReader, ushort>? read16Delegate = null;
    private Func<BinaryReader, uint>? read32Delegate = null;

    public Wwise_RIFF_Vorbis(
        string name,
        string codebooks_name,
        bool inline_codebooks,
        bool full_setup,
        ForcePacketFormat force_packet_format
    )
    {
        this._file_name = name;
        this._codebooks_name = codebooks_name;
        this._infile = new BinaryReader(File.OpenRead(name));
        this._inline_codebooks = inline_codebooks;
        this._full_setup = full_setup;
        if (_infile == null)
        {
            throw new FileOpenException(name);
        }

        _infile.seekg(0, StreamPosition.End);
        _file_size = _infile.tellg();

        {
            // check RIFF header
            _infile.seekg(0, StreamPosition.Beginning);
            byte[] riff_head = _infile.ReadBytes(4);

            if (CppUtils.memcmp(riff_head, "RIFX", 4))
            {
                if (CppUtils.memcmp(riff_head, "RIFF", 4))
                {
                    throw new ParseException("missing RIFF");
                }
                else
                {
                    _little_endian = true;
                }
            }
            else
            {
                _little_endian = false;
            }

            if (_little_endian)
            {
                read16Delegate = EndianReadWriteMethods.Read16LE;
                read32Delegate = EndianReadWriteMethods.Read32LE;
            }
            else
            {
                read16Delegate = EndianReadWriteMethods.Read16BE;
                read32Delegate = EndianReadWriteMethods.Read32BE;
            }

            _riff_size = (int)(read32Delegate(_infile) + 8);

            if (_riff_size > _file_size)
            {
                throw new ParseException("RIFF truncated");
            }

            byte[] wave_head = _infile.ReadBytes(4);
            if (CppUtils.memcmp(wave_head, "WAVE", 4))
            {
                throw new ParseException("missing WAVE");
            }
        }

        // read chunks
        int chunk_offset = 12;
        while (chunk_offset < _riff_size)
        {
            _infile.seekg(chunk_offset, StreamPosition.Beginning);

            if (chunk_offset + 8 > _riff_size)
            {
                throw new ParseException("chunk header truncated");
            }

            byte[] chunk_type = _infile.ReadBytes(4);
            uint chunk_size;

            chunk_size = read32Delegate(_infile);

            if (!CppUtils.memcmp(chunk_type, "fmt ", 4))
            {
                _fmt_offset = chunk_offset + 8;
                _fmt_size = (int)chunk_size;
            }
            else if (!CppUtils.memcmp(chunk_type, "cue ", 4))
            {
                _cue_offset = chunk_offset + 8;
                _cue_size = (int)chunk_size;
            }
            else if (!CppUtils.memcmp(chunk_type, "LIST", 4))
            {
                _LIST_offset = chunk_offset + 8;
                _LIST_size = (int)chunk_size;
            }
            else if (!CppUtils.memcmp(chunk_type, "smpl", 4))
            {
                _smpl_offset = chunk_offset + 8;
                _smpl_size = (int)chunk_size;
            }
            else if (!CppUtils.memcmp(chunk_type, "vorb", 4))
            {
                _vorb_offset = chunk_offset + 8;
                _vorb_size = (int)chunk_size;
            }
            else if (!CppUtils.memcmp(chunk_type, "data", 4))
            {
                _data_offset = chunk_offset + 8;
                _data_size = (int)chunk_size;
            }

            chunk_offset = (int)(chunk_offset + 8 + chunk_size);
        }

        if (chunk_offset > _riff_size)
        {
            throw new ParseException("chunk truncated");
        }

        // check that we have the chunks we're expecting
        if (-1 == _fmt_offset && -1 == _data_offset)
        {
            throw new ParseException("expected fmt, data chunks");
        }

        // read fmt
        if (-1 == _vorb_offset && 0x42 != _fmt_size)
        {
            throw new ParseException("expected 0x42 fmt if vorb missing");
        }

        if (-1 != _vorb_offset && 0x28 != _fmt_size && 0x18 != _fmt_size && 0x12 != _fmt_size)
        {
            throw new ParseException("bad fmt size");
        }

        if (-1 == _vorb_offset && 0x42 == _fmt_size)
        {
            // fake it out
            _vorb_offset = _fmt_offset + 0x18;
        }

        _infile.seekg(_fmt_offset, StreamPosition.Beginning);
        if ((ushort)(0xFFFF) != read16Delegate(_infile))
        {
            throw new ParseException("bad codec id");
        }
        _channels = read16Delegate(_infile);
        _sample_rate = read32Delegate(_infile);
        _avg_bytes_per_second = read32Delegate(_infile);
        if (0U != read16Delegate(_infile))
        {
            throw new ParseException("bad block align");
        }
        if (0U != read16Delegate(_infile))
        {
            throw new ParseException("expected 0 bps");
        }
        if (_fmt_size - 0x12 != read16Delegate(_infile))
        {
            throw new ParseException("bad extra fmt length");
        }

        if (_fmt_size - 0x12 >= 2)
        {
            // read extra fmt
            _ext_unk = read16Delegate(_infile);
            if (_fmt_size - 0x12 >= 6)
            {
                _subtype = read32Delegate(_infile);
            }
        }

        if (_fmt_size == 0x28)
        {
            byte[] whoknowsbuf = _infile.ReadBytes(16);
            byte[] whoknowsbuf_check =
            {
                1,
                0,
                0,
                0,
                0,
                0,
                0x10,
                0,
                0x80,
                0,
                0,
                0xAA,
                0,
                0x38,
                0x9b,
                0x71,
            };
            if (CppUtils.memcmp(whoknowsbuf, whoknowsbuf_check, 16))
            {
                throw new ParseException("expected signature in extra fmt?");
            }
        }

        // read cue
        if (-1 != _cue_offset)
        {
#if false
    //        if (0x1c != _cue_size) throw Parse_error_str("bad cue size");
#endif
            _infile.seekg(_cue_offset);

            _cue_count = read32Delegate(_infile);
        }

        // read LIST
        if (-1 != _LIST_offset)
        {
#if false
    //        if ( 4 != _LIST_size ) throw Parse_error_str("bad LIST size");
    //        char adtlbuf[4];
    //        const char adtlbuf_check[4] = {'a','d','t','l'};
    //        _infile.seekg(_LIST_offset);
    //        _infile.read(adtlbuf, 4);
    //        if (memcmp(adtlbuf, adtlbuf_check, 4)) throw Parse_error_str("expected only adtl in LIST");
#endif
        }

        // read smpl
        if (-1 != _smpl_offset)
        {
            _infile.seekg(_smpl_offset + 0x1C);
            _loop_count = read32Delegate(_infile);

            if (1 != _loop_count)
            {
                throw new ParseException("expected one loop");
            }

            _infile.seekg(_smpl_offset + 0x2c);
            _loop_start = read32Delegate(_infile);
            _loop_end = read32Delegate(_infile);
        }

        // read vorb
        switch (_vorb_size)
        {
            case -1:
            case 0x28:
            case 0x2A:
            case 0x2C:
            case 0x32:
            case 0x34:
                _infile.seekg(_vorb_offset + 0x00, StreamPosition.Beginning);
                break;

            default:
                throw new ParseException("bad vorb size");
        }

        _sample_count = read32Delegate(_infile);

        switch (_vorb_size)
        {
            case -1:
            case 0x2A:
            {
                _no_granule = true;

                _infile.seekg(_vorb_offset + 0x4, StreamPosition.Beginning);
                uint mod_signal = read32Delegate(_infile);

                // set
                // D9     11011001
                // CB     11001011
                // BC     10111100
                // B2     10110010
                // unset
                // 4A     01001010
                // 4B     01001011
                // 69     01101001
                // 70     01110000
                // A7     10100111 !!!

                // seems to be 0xD9 when _mod_packets should be set
                // also seen 0xCB, 0xBC, 0xB2
                if (
                    0x4A != mod_signal
                    && 0x4B != mod_signal
                    && 0x69 != mod_signal
                    && 0x70 != mod_signal
                )
                {
                    _mod_packets = true;
                }
                _infile.seekg(_vorb_offset + 0x10, StreamPosition.Beginning);
                break;
            }

            default:
                _infile.seekg(_vorb_offset + 0x18, StreamPosition.Beginning);
                break;
        }

        if (force_packet_format == ForcePacketFormat.ForceNoModPackets)
        {
            _mod_packets = false;
        }
        else if (force_packet_format == ForcePacketFormat.ForceModPackets)
        {
            _mod_packets = true;
        }

        _setup_packet_offset = read32Delegate(_infile);
        _first_audio_packet_offset = read32Delegate(_infile);

        switch (_vorb_size)
        {
            case -1:
            case 0x2A:
                _infile.seekg(_vorb_offset + 0x24, StreamPosition.Beginning);
                break;

            case 0x32:
            case 0x34:
                _infile.seekg(_vorb_offset + 0x2C, StreamPosition.Beginning);
                break;
        }

        switch (_vorb_size)
        {
            case 0x28:
            case 0x2C:
                // ok to leave _uid, _blocksize_0_pow and _blocksize_1_pow unset
                _header_triad_present = true;
                _old_packet_headers = true;
                break;

            case -1:
            case 0x2A:
            case 0x32:
            case 0x34:
                _uid = read32Delegate(_infile);
                _blocksize_0_pow = _infile.ReadByte();
                _blocksize_1_pow = _infile.ReadByte();
                break;
        }

        // check/set loops now that we know total sample count
        if (0 != _loop_count)
        {
            if (_loop_end == 0)
            {
                _loop_end = _sample_count;
            }
            else
            {
                _loop_end = _loop_end + 1;
            }

            if (
                _loop_start >= _sample_count
                || _loop_end > _sample_count
                || _loop_start > _loop_end
            )
            {
                throw new ParseException("loops out of range");
            }
        }

        // check subtype now that we know the vorb info
        // this is clearly just the channel layout
        switch (_subtype)
        {
            case 4: // 1 channel, no seek table
            case 3: // 2 channels
            case 0x33: // 4 channels
            case 0x37: // 5 channels, seek or not
            case 0x3b: // 5 channels, no seek table
            case 0x3f: // 6 channels, no seek table
                break;
            default:
                //throw Parse_error_str("unknown subtype");
                break;
        }
    }

    public void PrintInfo()
    {
        string fileType = _little_endian ? "RIFF WAVE" : "RIFX WAVE";

        string channelString;
        if (_channels != 1)
            channelString = $"{_channels} channels";
        else
            channelString = $"{_channels} channel";

        // logging removed
    }

    internal void GenerateOgg(BinaryWriter of)
    {
        BitOggStream os = new BitOggStream(of);

        bool[]? mode_blockflag = null;
        int mode_bits = 0;
        bool prev_blockflag = false;

        if (_header_triad_present)
        {
            generate_ogg_header_with_triad(os);
        }
        else
        {
            generate_ogg_header(os, ref mode_blockflag, ref mode_bits);
        }

        {
            // Audio pages
            int offset = (int)(_data_offset + _first_audio_packet_offset);

            while (offset < _data_offset + _data_size)
            {
                uint size;
                uint granule;
                int packet_header_size;
                int packet_payload_offset;
                int next_offset;

                if (_old_packet_headers)
                {
                    Packet8 audio_packet = new Packet8(_infile, offset, _little_endian);
                    packet_header_size = audio_packet.header_size();
                    size = audio_packet.size();
                    packet_payload_offset = audio_packet.offset();
                    granule = audio_packet.granule();
                    next_offset = audio_packet.next_offset();
                }
                else
                {
                    Packet audio_packet = new Packet(_infile, offset, _little_endian, _no_granule);
                    packet_header_size = audio_packet.header_size();
                    size = audio_packet.size();
                    packet_payload_offset = audio_packet.offset();
                    granule = audio_packet.granule();
                    next_offset = audio_packet.next_offset();
                }

                if (offset + packet_header_size > _data_offset + _data_size)
                {
                    throw new ParseException("page header truncated");
                }

                offset = packet_payload_offset;

                _infile.seekg(offset);
                // HACK: don't know what to do here
                if (granule == (uint)(0xFFFFFFFF))
                {
                    os.set_granule(1);
                }
                else
                {
                    os.set_granule(granule);
                }

                // first byte
                if (_mod_packets)
                {
                    // need to rebuild packet type and window info

                    if (mode_blockflag == null)
                    {
                        throw new ParseException("didn't load mode_blockflag");
                    }

                    // OUT: 1 bit packet type (0 == audio)
                    Bit_uint1 packet_type = new(0);
                    Bit_uint.WriteBits(os, packet_type);

                    Bit_uintv? mode_number_p = null;
                    Bit_uintv? remainder_p = null;

                    {
                        // collect mode number from first byte

                        BitStream ss = new BitStream(_infile);

                        // IN/OUT: N bit mode number (max 6 bits)
                        mode_number_p = new Bit_uintv((uint)mode_bits);
                        Bit_uintv.ReadBits(ss, mode_number_p);
                        Bit_uintv.WriteBits(os, mode_number_p);

                        // IN: remaining bits of first (input) byte
                        remainder_p = new Bit_uintv((uint)(8 - mode_bits));
                        Bit_uintv.ReadBits(ss, remainder_p);
                    }

                    if (mode_blockflag[mode_number_p!])
                    {
                        // long window, peek at next frame

                        _infile.seekg(next_offset);
                        bool next_blockflag = false;
                        if (next_offset + packet_header_size <= _data_offset + _data_size)
                        {
                            // mod_packets always goes with 6-byte headers
                            Packet audio_packet = new Packet(
                                _infile,
                                next_offset,
                                _little_endian,
                                _no_granule
                            );
                            uint next_packet_size = audio_packet.size();
                            if (next_packet_size > 0)
                            {
                                _infile.seekg(audio_packet.offset());

                                BitStream ss = new BitStream(_infile);
                                Bit_uintv next_mode_number = new Bit_uintv((uint)mode_bits);

                                Bit_uintv.ReadBits(ss, next_mode_number);

                                next_blockflag = mode_blockflag[next_mode_number];
                            }
                        }

                        // OUT: previous window type bit
                        Bit_uint1 prev_window_type = new(prev_blockflag ? 1U : 0);
                        Bit_uint.WriteBits(os, prev_window_type);

                        // OUT: next window type bit
                        Bit_uint1 next_window_type = new(next_blockflag ? 1U : 0);
                        Bit_uint.WriteBits(os, next_window_type);

                        // fix seek for rest of stream
                        _infile.seekg(offset + 1);
                    }

                    prev_blockflag = mode_blockflag[mode_number_p!];
                    mode_number_p = null;

                    // OUT: remaining bits of first (input) byte
                    Bit_uintv.WriteBits(os, remainder_p!);
                    remainder_p = null;
                }
                else
                {
                    // nothing unusual for first byte
                    Bit_uint8 c = new(_infile.ReadByte());
                    Bit_uint.WriteBits(os, c);
                }

                // remainder of packet
                for (uint i = 1; i < size; i++)
                {
                    Bit_uint8 c = new(_infile.ReadByte());
                    Bit_uint.WriteBits(os, c);
                }

                offset = next_offset;
                os.flush_page(false, (offset == _data_offset + _data_size));
            }
            if (offset > _data_offset + _data_size)
            {
                throw new ParseException("page truncated");
            }
        }

        mode_blockflag = null!;
    }

    private void generate_ogg_header(BitOggStream os, ref bool[]? mode_blockflag, ref int mode_bits)
    {
        {
            // generate identification packet
            VorbisPacketHeader vhead = new VorbisPacketHeader(1);

            VorbisPacketHeader.WriteHeader(os, vhead);

            Bit_uint32 version = new(0);
            Bit_uint.WriteBits(os, version);

            Bit_uint8 ch = new(_channels);
            Bit_uint.WriteBits(os, ch);

            Bit_uint32 srate = new(_sample_rate);
            Bit_uint.WriteBits(os, srate);

            Bit_uint32 bitrate_max = new(0);
            Bit_uint.WriteBits(os, bitrate_max);

            Bit_uint32 bitrate_nominal = new(_avg_bytes_per_second * 8);
            Bit_uint.WriteBits(os, bitrate_nominal);

            Bit_uint32 bitrate_minimum = new(0);
            Bit_uint.WriteBits(os, bitrate_minimum);

            Bit_uint4 blocksize_0 = new(_blocksize_0_pow);
            Bit_uint.WriteBits(os, blocksize_0);

            Bit_uint4 blocksize_1 = new(_blocksize_1_pow);
            Bit_uint.WriteBits(os, blocksize_1);

            Bit_uint1 framing = new(1);
            Bit_uint.WriteBits(os, framing);

            // identification packet on its own page
            os.flush_page();
        }

        {
            // generate comment packet
            VorbisPacketHeader vhead = new VorbisPacketHeader(3);

            VorbisPacketHeader.WriteHeader(os, vhead);

            const string vendor = "converted from Audiokinetic Wwise by ww2ogg";
            Bit_uint32 vendor_size = new((uint)vendor.Length);
            Bit_uint.WriteBits(os, vendor_size);

            for (int i = 0; i < vendor_size; i++)
            {
                Bit_uint8 c = new(vendor[i]);
                Bit_uint.WriteBits(os, c);
            }

            if (0 == _loop_count)
            {
                // no user comments
                Bit_uint32 user_comment_count = new(0);
                Bit_uint.WriteBits(os, user_comment_count);
            }
            else
            {
                // two comments, loop start and end
                Bit_uint32 user_comment_count = new(2);
                Bit_uint.WriteBits(os, user_comment_count);

                StringStream loop_start_str = new StringStream();
                StringStream loop_end_str = new StringStream();

                loop_start_str.Add($"LoopStart={_loop_start}");
                loop_end_str.Add("LoopEnd=").Add(_loop_end);

                Bit_uint32 loop_start_comment_length = new((uint)loop_start_str.str().Length);
                Bit_uint.WriteBits(os, loop_start_comment_length);
                for (uint i = 0; i < loop_start_comment_length; i++)
                {
                    Bit_uint8 c = new(loop_start_str.str().ToByteArray()[i]);
                    Bit_uint.WriteBits(os, c);
                }

                Bit_uint8 loop_end_comment_length = new((uint)loop_end_str.str().Length);
                Bit_uint.WriteBits(os, loop_end_comment_length);
                for (uint i = 0; i < loop_end_comment_length; i++)
                {
                    Bit_uint8 c = new(loop_end_str.str().ToByteArray()[i]);
                    Bit_uint.WriteBits(os, c);
                }
            }

            Bit_uint1 framing = new(1);
            Bit_uint.WriteBits(os, framing);

            //os.flush_bits();
            os.flush_page();
        }

        {
            // generate setup packet
            VorbisPacketHeader vhead = new VorbisPacketHeader(5);

            VorbisPacketHeader.WriteHeader(os, vhead);

            Packet setup_packet = new Packet(
                _infile,
                (int)(_data_offset + _setup_packet_offset),
                _little_endian,
                _no_granule
            );

            _infile.seekg(setup_packet.offset());
            if (setup_packet.granule() != 0)
            {
                throw new ParseException("setup packet granule != 0");
            }
            BitStream ss = new BitStream(_infile);

            // codebook count
            Bit_uint8 codebook_count_less1 = new();
            Bit_uint.ReadBits(ss, codebook_count_less1);
            uint codebook_count = codebook_count_less1 + 1;
            Bit_uint.WriteBits(os, codebook_count_less1);
            // logging removed

            // rebuild codebooks
            if (_inline_codebooks)
            {
                CodebookLibrary cbl = new CodebookLibrary();

                for (uint i = 0; i < codebook_count; i++)
                {
                    if (_full_setup)
                    {
                        cbl.copy(ss, os);
                    }
                    else
                    {
                        cbl.rebuild(ss, 0, os);
                    }
                }
            }
            else
            {
                /* external codebooks */

                CodebookLibrary cbl = new CodebookLibrary(_codebooks_name);

                for (uint i = 0; i < codebook_count; i++)
                {
                    Bit_uint10 codebook_id = new();
                    Bit_uint.ReadBits(ss, codebook_id);
                    try
                    {
                        cbl.rebuild((int)codebook_id, os);
                    }
                    catch (InvalidIdException)
                    {
                        //         B         C         V
                        //    4    2    4    3    5    6
                        // 0100 0010 0100 0011 0101 0110
                        // \_______|____ ___|/
                        //              X
                        //            11 0100 0010

                        if (codebook_id == 0x342)
                        {
                            Bit_uint14 codebook_identifier = new();
                            Bit_uint.ReadBits(ss, codebook_identifier);

                            //         B         C         V
                            //    4    2    4    3    5    6
                            // 0100 0010 0100 0011 0101 0110
                            //           \_____|_ _|_______/
                            //                   X
                            //         01 0101 10 01 0000
                            if (codebook_identifier == 0x1590)
                            {
                                // starts with BCV, probably --full-setup
                                throw new ParseException(
                                    "invalid codebook id 0x342, try --full-setup"
                                );
                            }
                        }

                        // just an invalid codebook
                        throw;
                    }
                }
            }

            // Time Domain transforms (placeholder)
            Bit_uint6 time_count_less1 = new(0);
            Bit_uint.WriteBits(os, time_count_less1);
            Bit_uint16 dummy_time_value = new(0);
            Bit_uint.WriteBits(os, dummy_time_value);

            if (_full_setup)
            {
                while (ss.GetTotalBitsRead() < setup_packet.size() * 8u)
                {
                    Bit_uint1 bitly = new();
                    Bit_uint.ReadBits(ss, bitly);
                    Bit_uint.WriteBits(os, bitly);
                }
            }
            else // _full_setup
            {
                // floor count
                Bit_uint6 floor_count_less1 = new();
                Bit_uint.ReadBits(ss, floor_count_less1);
                uint floor_count = floor_count_less1 + 1;
                Bit_uint.WriteBits(os, floor_count_less1);

                // rebuild floors
                for (uint i = 0; i < floor_count; i++)
                {
                    // Always floor type 1
                    Bit_uint16 floor_type = new(1);
                    Bit_uint.WriteBits(os, floor_type);

                    Bit_uint5 floor1_partitions = new();
                    Bit_uint.ReadBits(ss, floor1_partitions);
                    Bit_uint.WriteBits(os, floor1_partitions);

                    uint[] floor1_partition_class_list = new uint[floor1_partitions];

                    uint maximum_class = 0;
                    for (uint j = 0; j < floor1_partitions; j++)
                    {
                        Bit_uint4 floor1_partition_class = new();
                        Bit_uint.ReadBits(ss, floor1_partition_class);
                        Bit_uint.WriteBits(os, floor1_partition_class);

                        floor1_partition_class_list[j] = floor1_partition_class;

                        if (floor1_partition_class > maximum_class)
                        {
                            maximum_class = floor1_partition_class;
                        }
                    }

                    uint[] floor1_class_dimensions_list = new uint[maximum_class + 1];

                    for (uint j = 0; j <= maximum_class; j++)
                    {
                        Bit_uint3 class_dimensions_less1 = new();
                        Bit_uint.ReadBits(ss, class_dimensions_less1);
                        Bit_uint.WriteBits(os, class_dimensions_less1);

                        floor1_class_dimensions_list[j] = class_dimensions_less1 + 1;

                        Bit_uint2 class_subclasses = new();
                        Bit_uint.ReadBits(ss, class_subclasses);
                        Bit_uint.WriteBits(os, class_subclasses);

                        if (0 != class_subclasses)
                        {
                            Bit_uint8 masterbook = new();
                            Bit_uint.ReadBits(ss, masterbook);
                            Bit_uint.WriteBits(os, masterbook);

                            if (masterbook >= codebook_count)
                            {
                                throw new ParseException("invalid floor1 masterbook");
                            }
                        }

                        for (uint k = 0; k < (1U << (int)class_subclasses); k++)
                        {
                            Bit_uint8 subclass_book_plus1 = new();
                            Bit_uint.ReadBits(ss, subclass_book_plus1);
                            Bit_uint.WriteBits(os, subclass_book_plus1);

                            int subclass_book = (int)subclass_book_plus1 - 1;
                            if (subclass_book >= 0 && (uint)subclass_book >= codebook_count)
                            {
                                throw new ParseException("invalid floor1 subclass book");
                            }
                        }
                    }

                    Bit_uint2 floor1_multiplier_less1 = new();
                    Bit_uint.ReadBits(ss, floor1_multiplier_less1);
                    Bit_uint.WriteBits(os, floor1_multiplier_less1);

                    Bit_uint4 rangebits = new();
                    Bit_uint.ReadBits(ss, rangebits);
                    Bit_uint.WriteBits(os, rangebits);

                    for (uint j = 0; j < floor1_partitions; j++)
                    {
                        uint current_class_number = floor1_partition_class_list[j];
                        for (
                            uint k = 0;
                            k < floor1_class_dimensions_list[current_class_number];
                            k++
                        )
                        {
                            Bit_uintv X = new Bit_uintv(rangebits);
                            Bit_uintv.ReadBits(ss, X);
                            Bit_uintv.WriteBits(os, X);
                        }
                    }

                    floor1_class_dimensions_list = null!;
                    floor1_partition_class_list = null!;
                }

                // residue count
                Bit_uint6 residue_count_less1 = new();
                Bit_uint.ReadBits(ss, residue_count_less1);
                Bit_uint.WriteBits(os, residue_count_less1);
                uint residue_count = residue_count_less1 + 1;

                // rebuild residues
                for (uint i = 0; i < residue_count; i++)
                {
                    Bit_uint2 residue_type = new();
                    Bit_uint.ReadBits(ss, residue_type);
                    Bit_uint.WriteBits(os, new Bit_uint16(residue_type));

                    if (residue_type > 2)
                    {
                        throw new ParseException("invalid residue type");
                    }

                    Bit_uint24 residue_begin = new();
                    Bit_uint24 residue_end = new();
                    Bit_uint24 residue_partition_size_less1 = new();
                    Bit_uint6 residue_classifications_less1 = new();
                    Bit_uint8 residue_classbook = new();

                    Bit_uint.ReadBits(ss, residue_begin);
                    Bit_uint.ReadBits(ss, residue_end);
                    Bit_uint.ReadBits(ss, residue_partition_size_less1);
                    Bit_uint.ReadBits(ss, residue_classifications_less1);
                    Bit_uint.ReadBits(ss, residue_classbook);
                    uint residue_classifications = residue_classifications_less1 + 1;
                    Bit_uint.WriteBits(os, residue_begin);
                    Bit_uint.WriteBits(os, residue_end);
                    Bit_uint.WriteBits(os, residue_partition_size_less1);
                    Bit_uint.WriteBits(os, residue_classifications_less1);
                    Bit_uint.WriteBits(os, residue_classbook);

                    if (residue_classbook >= codebook_count)
                    {
                        throw new ParseException("invalid residue classbook");
                    }

                    uint[] residue_cascade = new uint[residue_classifications];

                    for (uint j = 0; j < residue_classifications; j++)
                    {
                        Bit_uint5 high_bits = new(0);
                        Bit_uint3 low_bits = new();

                        Bit_uint.ReadBits(ss, low_bits);
                        Bit_uint.WriteBits(os, low_bits);

                        Bit_uint1 bitflag = new();
                        Bit_uint.ReadBits(ss, bitflag);
                        Bit_uint.WriteBits(os, bitflag);
                        if (bitflag != 0)
                        {
                            Bit_uint.ReadBits(ss, high_bits);
                            Bit_uint.WriteBits(os, high_bits);
                        }

                        residue_cascade[j] = high_bits * 8 + low_bits;
                    }

                    for (uint j = 0; j < residue_classifications; j++)
                    {
                        for (uint k = 0; k < 8; k++)
                        {
                            if ((residue_cascade[j] & (1 << (int)k)) != 0)
                            {
                                Bit_uint8 residue_book = new();
                                Bit_uint.ReadBits(ss, residue_book);
                                Bit_uint.WriteBits(os, residue_book);

                                if (residue_book >= codebook_count)
                                {
                                    throw new ParseException("invalid residue book");
                                }
                            }
                        }
                    }

                    residue_cascade = null!;
                }

                // mapping count
                Bit_uint6 mapping_count_less1 = new();
                Bit_uint.ReadBits(ss, mapping_count_less1);
                Bit_uint.WriteBits(os, mapping_count_less1);
                uint mapping_count = mapping_count_less1 + 1;

                for (uint i = 0; i < mapping_count; i++)
                {
                    // always mapping type 0, the only one
                    Bit_uint16 mapping_type = new(0);

                    Bit_uint.WriteBits(os, mapping_type);

                    Bit_uint1 submaps_flag = new();
                    Bit_uint.ReadBits(ss, submaps_flag);
                    Bit_uint.WriteBits(os, submaps_flag);

                    uint submaps = 1;
                    if (submaps_flag != 0)
                    {
                        Bit_uint4 submaps_less1 = new();

                        Bit_uint.ReadBits(ss, submaps_less1);
                        Bit_uint.WriteBits(os, submaps_less1);
                        submaps = submaps_less1 + 1;
                    }

                    Bit_uint1 square_polar_flag = new();
                    Bit_uint.ReadBits(ss, square_polar_flag);
                    Bit_uint.WriteBits(os, square_polar_flag);

                    if (square_polar_flag != 0)
                    {
                        Bit_uint8 coupling_steps_less1 = new();
                        Bit_uint.ReadBits(ss, coupling_steps_less1);
                        Bit_uint.WriteBits(os, coupling_steps_less1);
                        uint coupling_steps = coupling_steps_less1 + 1;

                        for (uint j = 0; j < coupling_steps; j++)
                        {
                            Bit_uintv magnitude = new Bit_uintv(
                                (uint)EndianReadWriteMethods.ilog((uint)(_channels - 1))
                            );
                            Bit_uintv angle = new Bit_uintv(
                                (uint)EndianReadWriteMethods.ilog((uint)(_channels - 1))
                            );

                            Bit_uintv.ReadBits(ss, magnitude);
                            Bit_uintv.ReadBits(ss, angle);
                            Bit_uintv.WriteBits(os, magnitude);
                            Bit_uintv.WriteBits(os, angle);

                            if (angle == magnitude || magnitude >= _channels || angle >= _channels)
                            {
                                throw new ParseException("invalid coupling");
                            }
                        }
                    }

                    // a rare reserved field not removed by Ak!
                    Bit_uint2 mapping_reserved = new();
                    Bit_uint.ReadBits(ss, mapping_reserved);
                    Bit_uint.WriteBits(os, mapping_reserved);
                    if (0 != mapping_reserved)
                    {
                        throw new ParseException("mapping reserved field nonzero");
                    }

                    if (submaps > 1)
                    {
                        for (uint j = 0; j < _channels; j++)
                        {
                            Bit_uint4 mapping_mux = new();
                            Bit_uint.ReadBits(ss, mapping_mux);
                            Bit_uint.WriteBits(os, mapping_mux);

                            if (mapping_mux >= submaps)
                            {
                                throw new ParseException("mapping_mux >= submaps");
                            }
                        }
                    }

                    for (uint j = 0; j < submaps; j++)
                    {
                        // Another! Unused time domain transform configuration placeholder!
                        Bit_uint8 time_config = new();
                        Bit_uint.ReadBits(ss, time_config);
                        Bit_uint.WriteBits(os, time_config);

                        Bit_uint8 floor_number = new();
                        Bit_uint.ReadBits(ss, floor_number);
                        Bit_uint.WriteBits(os, floor_number);
                        if (floor_number >= floor_count)
                        {
                            throw new ParseException("invalid floor mapping");
                        }

                        Bit_uint8 residue_number = new();
                        Bit_uint.ReadBits(ss, residue_number);
                        Bit_uint.WriteBits(os, residue_number);
                        if (residue_number >= residue_count)
                        {
                            throw new ParseException("invalid residue mapping");
                        }
                    }
                }

                // mode count
                Bit_uint6 mode_count_less1 = new();
                Bit_uint.ReadBits(ss, mode_count_less1);
                Bit_uint.WriteBits(os, mode_count_less1);
                uint mode_count = mode_count_less1 + 1;

                mode_blockflag = new bool[mode_count];
                mode_bits = EndianReadWriteMethods.ilog(mode_count - 1);

                for (uint i = 0; i < mode_count; i++)
                {
                    Bit_uint1 block_flag = new();
                    Bit_uint.ReadBits(ss, block_flag);
                    Bit_uint.WriteBits(os, block_flag);

                    mode_blockflag[i] = (block_flag != 0);

                    // only 0 valid for windowtype and transformtype
                    Bit_uint16 windowtype = new(0);
                    Bit_uint16 transformtype = new(0);
                    Bit_uint.WriteBits(os, windowtype);
                    Bit_uint.WriteBits(os, transformtype);

                    Bit_uint8 mapping = new();
                    Bit_uint.ReadBits(ss, mapping);
                    Bit_uint.WriteBits(os, mapping);
                    if (mapping >= mapping_count)
                    {
                        throw new ParseException("invalid mode mapping");
                    }
                }

                Bit_uint1 framing = new(1);
                Bit_uint.WriteBits(os, framing);
            } // _full_setup

            os.flush_page();

            if ((ss.GetTotalBitsRead() + 7) / 8 != setup_packet.size())
            {
                throw new ParseException("didn't read exactly setup packet");
            }

            if (setup_packet.next_offset() != _data_offset + (int)_first_audio_packet_offset)
            {
                throw new ParseException("first audio packet doesn't follow setup packet");
            }
        }
    }

    private void generate_ogg_header_with_triad(BitOggStream os)
    {
        {
            // Header page triad
            int offset = (int)(_data_offset + _setup_packet_offset);

            {
                // copy information packet
                Packet8 information_packet = new Packet8(_infile, offset, _little_endian);
                uint size = information_packet.size();

                if (information_packet.granule() != 0)
                {
                    throw new ParseException("information packet granule != 0");
                }

                _infile.seekg(information_packet.offset());

                Bit_uint6 c = new((uint)_infile.ReadByte());
                if (1 != c)
                {
                    throw new ParseException("wrong type for information packet");
                }

                Bit_uint.WriteBits(os, c);

                for (uint i = 1; i < size; i++)
                {
                    c.CopyFrom((uint)_infile.ReadByte());
                    Bit_uint.WriteBits(os, c);
                }

                // identification packet on its own page
                os.flush_page();

                offset = information_packet.next_offset();
            }

            {
                // copy comment packet
                Packet8 comment_packet = new Packet8(_infile, offset, _little_endian);
                ushort size = (ushort)comment_packet.size();

                if (comment_packet.granule() != 0)
                {
                    throw new ParseException("comment packet granule != 0");
                }

                _infile.seekg(comment_packet.offset());

                Bit_uint8 c = new((uint)_infile.ReadByte());
                if (3 != c)
                {
                    throw new ParseException("wrong type for comment packet");
                }

                Bit_uint.WriteBits(os, c);

                for (uint i = 1; i < size; i++)
                {
                    c.CopyFrom((uint)_infile.ReadByte());
                    Bit_uint.WriteBits(os, c);
                }

                // identification packet on its own page
                os.flush_page();

                offset = comment_packet.next_offset();
            }

            {
                // copy setup packet
                Packet8 setup_packet = new Packet8(_infile, offset, _little_endian);

                _infile.seekg(setup_packet.offset());
                if (setup_packet.granule() != 0)
                {
                    throw new ParseException("setup packet granule != 0");
                }
                BitStream ss = new BitStream(_infile);

                Bit_uint8 c = new();
                Bit_uint.ReadBits(ss, c);

                // type
                if (5 != c)
                {
                    throw new ParseException("wrong type for setup packet");
                }
                Bit_uint.WriteBits(os, c);

                // 'vorbis'
                for (uint i = 0; i < 6; i++)
                {
                    Bit_uint.ReadBits(ss, c);
                    Bit_uint.WriteBits(os, c);
                }

                // codebook count
                Bit_uint8 codebook_count_less1 = new();
                Bit_uint.ReadBits(ss, codebook_count_less1);
                Bit_uint.WriteBits(os, codebook_count_less1);
                uint codebook_count = codebook_count_less1 + 1;

                CodebookLibrary cbl = new CodebookLibrary();

                // rebuild codebooks
                for (uint i = 0; i < codebook_count; i++)
                {
                    cbl.copy(ss, os);
                }

                while (ss.GetTotalBitsRead() < setup_packet.size() * 8u)
                {
                    Bit_uint1 bitly = new();
                    Bit_uint.ReadBits(ss, bitly);
                    Bit_uint.WriteBits(os, bitly);
                }

                os.flush_page();

                offset = setup_packet.next_offset();
            }

            if (offset != _data_offset + (int)_first_audio_packet_offset)
            {
                throw new ParseException("first audio packet doesn't follow setup packet");
            }
        }
    }
}
