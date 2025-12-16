using System.IO;
using BnkExtractor.Ww2ogg.Exceptions;
using BnkExtractor.Ww2ogg.Extensions;

namespace BnkExtractor.Ww2ogg;

public class CodebookLibrary
{
    private byte[] codebook_data;
    private int[] codebook_offsets;
    private int codebook_count;

    public CodebookLibrary(string filename)
    {
        this.codebook_data = null;
        this.codebook_offsets = null;
        this.codebook_count = 0;
        BinaryReader @is = new BinaryReader(File.OpenRead(filename));

        if (@is == null)
        {
            throw new FileOpenException(filename);
        }

        @is.seekg(0, StreamPosition.End);
        int file_size = @is.tellg();

        @is.seekg(file_size - 4, StreamPosition.Beginning);
        int offset_offset = (int)EndianReadWriteMethods.Read32LE(@is);
        codebook_count = (file_size - offset_offset) / 4;

        codebook_data = new byte[offset_offset];
        codebook_offsets = new int[codebook_count];

        @is.seekg(0, StreamPosition.Beginning);
        for (int i = 0; i < offset_offset; i++)
        {
            codebook_data[i] = @is.ReadByte();
        }

        for (int i = 0; i < codebook_count; i++)
        {
            codebook_offsets[i] = (int)EndianReadWriteMethods.Read32LE(@is);
        }
    }

    public CodebookLibrary()
    {
        this.codebook_data = null;
        this.codebook_offsets = null;
        this.codebook_count = 0;
    }

    public byte[] get_codebook(int i)
    {
        if (codebook_data == null || codebook_offsets == null)
        {
            throw new ParseException("codebook library not loaded");
        }
        if (i >= codebook_count || i < 0)
        {
            return null;
        }

        return codebook_data.Subset(codebook_offsets[i], get_codebook_size(i));
    }

    public int get_codebook_size(int i)
    {
        if (codebook_data == null || codebook_offsets == null)
        {
            throw new ParseException("codebook library not loaded");
        }
        if (i >= codebook_count || i < 0)
        {
            return -1;
        }
        else if (i == codebook_count - 1)
        {
            return codebook_data.Length - codebook_offsets[i];
        }
        return codebook_offsets[i + 1] - codebook_offsets[i];
    }

    public void rebuild(int i, BitOggStream bos)
    {
        byte[] cb = get_codebook(i);

        int signed_cb_size = get_codebook_size(i);

        if (cb == null || -1 == signed_cb_size)
        {
            throw new InvalidIdException(i);
        }

        uint cb_size = (uint)signed_cb_size;

        BinaryReader @is = new BinaryReader(new MemoryStream(cb));
        BitStream bis = new BitStream(@is);

        rebuild(bis, cb_size, bos);
    }

    /* cb_size == 0 to not check size (for an inline bitstream) */
    internal void rebuild(BitStream bis, uint cb_size, BitOggStream bos)
    {
        /* IN: 4 bit dimensions, 14 bit entry count */

        Bit_uint4 dimensions = new();
        Bit_uint14 entries = new();
        Bit_uint.ReadBits(bis, dimensions);
        Bit_uint.ReadBits(bis, entries);

        // logging removed

        /* OUT: 24 bit identifier, 16 bit dimensions, 24 bit entry count */
        Bit_uint.WriteBits(bos, new Bit_uint24(0x564342));
        Bit_uint.WriteBits(bos, new Bit_uint16(dimensions));
        Bit_uint.WriteBits(bos, new Bit_uint24(entries));

        // gather codeword lengths

        /* IN/OUT: 1 bit ordered flag */
        Bit_uint1 ordered = new();
        Bit_uint.ReadBits(bis, ordered);
        Bit_uint.WriteBits(bos, ordered);

        if (ordered != 0)
        {
            /* IN/OUT: 5 bit initial length */
            Bit_uint5 initial_length = new();
            Bit_uint.ReadBits(bis, initial_length);
            Bit_uint.WriteBits(bos, initial_length);

            uint current_entry = 0;
            while (current_entry < entries)
            {
                /* IN/OUT: ilog(entries-current_entry) bit count w/ given length */
                Bit_uintv number = new Bit_uintv(
                    (uint)EndianReadWriteMethods.ilog(entries - current_entry)
                );
                Bit_uintv.ReadBits(bis, number);
                Bit_uintv.WriteBits(bos, number);
                current_entry += number;
            }
            if (current_entry > entries)
            {
                throw new ParseException("current_entry out of range");
            }
        }
        else
        {
            /* IN: 3 bit codeword length length, 1 bit sparse flag */
            Bit_uint3 codeword_length_length = new();
            Bit_uint1 sparse = new();
            Bit_uint.ReadBits(bis, codeword_length_length);
            Bit_uint.ReadBits(bis, sparse);

            // logging removed

            if (0 == codeword_length_length || 5 < codeword_length_length)
            {
                throw new ParseException("nonsense codeword length");
            }

            /* OUT: 1 bit sparse flag */
            Bit_uint.WriteBits(bos, sparse);

            for (uint i = 0; i < entries; i++)
            {
                bool present_bool = true;

                if (sparse != 0)
                {
                    /* IN/OUT 1 bit sparse presence flag */
                    Bit_uint1 present = new();
                    Bit_uint.ReadBits(bis, present);
                    Bit_uint.WriteBits(bos, present);

                    present_bool = (0 != present);
                }

                if (present_bool)
                {
                    /* IN: n bit codeword length-1 */
                    Bit_uintv codeword_length = new Bit_uintv(codeword_length_length);
                    Bit_uintv.ReadBits(bis, codeword_length);

                    /* OUT: 5 bit codeword length-1 */
                    Bit_uint.WriteBits(bos, new Bit_uint5(codeword_length));
                }
            }
        } // done with lengths

        // lookup table

        /* IN: 1 bit lookup type */
        Bit_uint1 lookup_type = new();
        Bit_uint.ReadBits(bis, lookup_type);
        /* OUT: 4 bit lookup type */
        Bit_uint.WriteBits(bos, new Bit_uint4(lookup_type));

        if (0 == lookup_type)
        {
            // logging removed
        }
        else if (1 == lookup_type)
        {
            // logging removed

            /* IN/OUT: 32 bit minimum length, 32 bit maximum length, 4 bit value length-1, 1 bit sequence flag */
            Bit_uint32 min = new();
            Bit_uint32 max = new();
            Bit_uint4 value_length = new();
            Bit_uint1 sequence_flag = new();
            Bit_uint.ReadBits(bis, min);
            Bit_uint.ReadBits(bis, max);
            Bit_uint.ReadBits(bis, value_length);
            Bit_uint.ReadBits(bis, sequence_flag);
            Bit_uint.WriteBits(bos, min);
            Bit_uint.WriteBits(bos, max);
            Bit_uint.WriteBits(bos, value_length);
            Bit_uint.WriteBits(bos, sequence_flag);

            uint quantvals = _book_maptype1_quantvals(entries, dimensions);
            for (uint i = 0; i < quantvals; i++)
            {
                /* IN/OUT: n bit value */
                Bit_uintv val = new Bit_uintv(value_length + 1);
                Bit_uintv.ReadBits(bis, val);
                Bit_uintv.WriteBits(bos, val);
            }
        }
        else if (2 == lookup_type)
        {
            throw new ParseException("didn't expect lookup type 2");
        }
        else
        {
            throw new ParseException("invalid lookup type");
        }

        // logging removed

        /* check that we used exactly all bytes */
        /* note: if all bits are used in the last byte there will be one extra 0 byte */
        if (0 != cb_size && bis.GetTotalBitsRead() / 8 + 1 != cb_size)
        {
            throw new SizeMismatchException(cb_size, bis.GetTotalBitsRead() / 8 + 1);
        }
    }

    /* cb_size == 0 to not check size (for an inline bitstream) */
    public void copy(BitStream bis, BitOggStream bos)
    {
        /* IN: 24 bit identifier, 16 bit dimensions, 24 bit entry count */

        Bit_uint24 id = new();
        Bit_uint16 dimensions = new();
        Bit_uint24 entries = new();

        Bit_uint.ReadBits(bis, id);
        Bit_uint.ReadBits(bis, dimensions);
        Bit_uint.ReadBits(bis, entries);

        if (0x564342 != id)
        {
            throw new ParseException("invalid codebook identifier");
        }

        // logging removed

        /* OUT: 24 bit identifier, 16 bit dimensions, 24 bit entry count */
        Bit_uint.WriteBits(bos, id);
        Bit_uint.WriteBits(bos, dimensions);
        Bit_uint.WriteBits(bos, entries);

        // gather codeword lengths

        /* IN/OUT: 1 bit ordered flag */
        Bit_uint1 ordered = new();
        Bit_uint.ReadBits(bis, ordered);
        Bit_uint.WriteBits(bos, ordered);
        if (ordered)
        {
            /* IN/OUT: 5 bit initial length */
            Bit_uint5 initial_length = new();
            Bit_uint.ReadBits(bis, initial_length);
            Bit_uint.WriteBits(bos, initial_length);

            uint current_entry = 0;
            while (current_entry < entries)
            {
                /* IN/OUT: ilog(entries-current_entry) bit count w/ given length */
                Bit_uintv number = new Bit_uintv(
                    (uint)EndianReadWriteMethods.ilog(entries - current_entry)
                );
                Bit_uintv.ReadBits(bis, number);
                Bit_uintv.WriteBits(bos, number);
                current_entry += number;
            }
            if (current_entry > entries)
            {
                throw new ParseException("current_entry out of range");
            }
        }
        else
        {
            /* IN/OUT: 1 bit sparse flag */
            Bit_uint1 sparse = new();
            Bit_uint.ReadBits(bis, sparse);
            Bit_uint.WriteBits(bos, sparse);

            // logging removed

            for (uint i = 0; i < entries; i++)
            {
                bool present_bool = true;

                if (sparse)
                {
                    /* IN/OUT 1 bit sparse presence flag */
                    Bit_uint1 present = new();
                    Bit_uint.ReadBits(bis, present);
                    Bit_uint.WriteBits(bos, present);

                    present_bool = (0 != present);
                }

                if (present_bool)
                {
                    /* IN/OUT: 5 bit codeword length-1 */
                    Bit_uint5 codeword_length = new();
                    Bit_uint.ReadBits(bis, codeword_length);
                    Bit_uint.WriteBits(bos, codeword_length);
                }
            }
        } // done with lengths

        // lookup table

        /* IN/OUT: 4 bit lookup type */
        Bit_uint4 lookup_type = new();
        Bit_uint.ReadBits(bis, lookup_type);
        Bit_uint.WriteBits(bos, lookup_type);

        if (0 == lookup_type)
        {
            // logging removed
        }
        else if (1 == lookup_type)
        {
            // logging removed

            /* IN/OUT: 32 bit minimum length, 32 bit maximum length, 4 bit value length-1, 1 bit sequence flag */
            Bit_uint32 min = new();
            Bit_uint32 max = new();
            Bit_uint4 value_length = new();
            Bit_uint1 sequence_flag = new();
            Bit_uint.ReadBits(bis, min);
            Bit_uint.ReadBits(bis, max);
            Bit_uint.ReadBits(bis, value_length);
            Bit_uint.ReadBits(bis, sequence_flag);
            Bit_uint.WriteBits(bos, min);
            Bit_uint.WriteBits(bos, max);
            Bit_uint.WriteBits(bos, value_length);
            Bit_uint.WriteBits(bos, sequence_flag);

            uint quantvals = _book_maptype1_quantvals(entries, dimensions);
            for (uint i = 0; i < quantvals; i++)
            {
                /* IN/OUT: n bit value */
                Bit_uintv val = new Bit_uintv(value_length + 1);
                Bit_uintv.ReadBits(bis, val);
                Bit_uintv.WriteBits(bos, val);
            }
        }
        else if (2 == lookup_type)
        {
            throw new ParseException("didn't expect lookup type 2");
        }
        else
        {
            throw new ParseException("invalid lookup type");
        }

        // logging removed
    }

    public static uint _book_maptype1_quantvals(uint entries, uint dimensions)
    {
        /* get us a starting hint, we'll polish it below */
        int bits = EndianReadWriteMethods.ilog(entries);
        uint vals = entries >> (int)((bits - 1) * (dimensions - 1) / dimensions);

        while (true)
        {
            uint acc = 1;
            uint acc1 = 1;
            uint i;
            for (i = 0; i < dimensions; i++)
            {
                acc *= vals;
                acc1 *= vals + 1;
            }
            if (acc <= entries && acc1 > entries)
            {
                return vals;
            }
            else
            {
                if (acc > entries)
                {
                    vals--;
                }
                else
                {
                    vals++;
                }
            }
        }
    }
}
