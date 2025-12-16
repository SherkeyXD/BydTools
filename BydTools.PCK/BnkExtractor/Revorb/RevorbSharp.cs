using System;
using System.IO;
using System.Runtime.InteropServices;
using OggVorbisSharp;
using static OggVorbisSharp.Ogg;
using static OggVorbisSharp.Vorbis;

namespace BnkExtractor.Revorb
{
    public static unsafe class RevorbSharp
    {
        private static bool Failed = false;

        /// <summary>
        /// Recomputes page granule positions in Ogg Vorbis files
        /// </summary>
        /// <param name="inputFilePath">The ogg file to have recalculated</param>
        /// <param name="outputFilePath">The destination path to save the outputted ogg file. If null or empty, it uses "Revorbed.ogg"</param>
        public static void Convert(string inputFilePath, string outputFilePath)
        {
            if (string.IsNullOrEmpty(outputFilePath))
            {
                outputFilePath = "Revorbed.ogg";
            }

            Convert(File.OpenRead(inputFilePath), File.Create(outputFilePath));
        }

        /// <summary>
        /// Recomputes page granule positions in Ogg Vorbis files
        /// </summary>
        /// <param name="inputData">Input ogg data for granule recalculation</param>
        /// <returns>A byte array containing the outputted ogg data</returns>
        public static byte[] Convert(byte[] inputData)
        {
            using MemoryStream inputStream = new MemoryStream(inputData);
            using MemoryStream outputStream = new MemoryStream();
            Convert(inputStream, outputStream);
            return outputStream.ToArray();
        }

        /// <summary>
        /// Recomputes page granule positions in Ogg Vorbis files
        /// </summary>
        /// <param name="inputStream">A stream to read the input data from</param>
        /// <param name="outputStream">A stream to write the output data to</param>
        public static void Convert(Stream inputStream, Stream outputStream)
        {
            // 重置失败标志
            Failed = false;

            using BinaryReader fi = new BinaryReader(inputStream);
            using BinaryWriter fo = new BinaryWriter(outputStream);

            ogg_sync_state sync_in = new ogg_sync_state();
            ogg_sync_state sync_out = new ogg_sync_state();
            ogg_sync_init(&sync_in);
            ogg_sync_init(&sync_out);

            ogg_stream_state stream_in = new ogg_stream_state();
            ogg_stream_state stream_out = new ogg_stream_state();
            vorbis_info vi = new vorbis_info();
            vorbis_info_init(&vi);

            ogg_packet packet = new ogg_packet();
            ogg_page page = new ogg_page();

            if (CopyHeaders(fi, &sync_in, &stream_in, fo, &sync_out, &stream_out, &vi))
            {
                long granpos = 0;
                long packetnum = 0;
                long lastbs = 0;

                while (true)
                {
                    int eos = 0;

                    while (eos == 0)
                    {
                        int res = ogg_sync_pageout(&sync_in, &page);

                        if (res == 0)
                        {
                            byte* buffer = ogg_sync_buffer(&sync_in, new CLong(4096));
                            int numread = FRead(buffer, 4096, fi);

                            if (numread > 0)
                            {
                                ogg_sync_wrote(&sync_in, new CLong(numread));
                            }
                            else
                            {
                                eos = 2;
                            }

                            continue;
                        }

                        if (res < 0)
                        {
                            Failed = true;
                        }
                        else
                        {
                            if (ogg_page_eos(&page) != 0)
                            {
                                eos = 1;
                            }

                            ogg_stream_pagein(&stream_in, &page);

                            while (true)
                            {
                                res = ogg_stream_packetout(&stream_in, &packet);

                                if (res == 0)
                                {
                                    break;
                                }
                                else if (res < 0)
                                {
                                    Failed = true;
                                    continue;
                                }

                                long bs = vorbis_packet_blocksize(&vi, &packet);

                                if (lastbs != 0)
                                {
                                    granpos += (lastbs + bs) / 4;
                                }

                                lastbs = bs;
                                packet.granulepos = granpos;
                                packet.packetno = packetnum++;

                                var eosValue = packet.e_o_s.Value;
                                if (eosValue == 0)
                                {
                                    ogg_stream_packetin(&stream_out, &packet);

                                    ogg_page opage = new ogg_page();

                                    while (ogg_stream_pageout(&stream_out, &opage) != 0)
                                    {
                                        if (
                                            FWrite(opage.header, opage.header_len, fo).Value
                                                != opage.header_len.Value
                                            || FWrite(opage.body, opage.body_len, fo).Value
                                                != opage.body_len.Value
                                        )
                                        {
                                            eos = 2;
                                            Failed = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (eos == 2)
                    {
                        break;
                    }

                    packet.e_o_s = new CLong(1);
                    ogg_stream_packetin(&stream_out, &packet);
                    ogg_page opage2 = new ogg_page();

                    while (ogg_stream_flush(&stream_out, &opage2) != 0)
                    {
                        if (
                            FWrite(opage2.header, opage2.header_len, fo).Value
                                != opage2.header_len.Value
                            || FWrite(opage2.body, opage2.body_len, fo).Value
                                != opage2.body_len.Value
                        )
                        {
                            Failed = true;
                            break;
                        }
                    }

                    ogg_stream_clear(&stream_in);
                    break;
                }

                ogg_stream_clear(&stream_out);
            }
            else
            {
                Failed = true;
            }

            vorbis_info_clear(&vi);

            ogg_sync_clear(&sync_in);
            ogg_sync_clear(&sync_out);

            // 如果 revorb 失败，抛出异常
            if (Failed)
            {
                throw new InvalidOperationException(
                    "Failed to revorb OGG file: granule position recalculation failed. The OGG file may be corrupted or in an unsupported format."
                );
            }
        }

        /// <summary>
        /// Reads data to a fixed buffer
        /// </summary>
        /// <param name="ptr">Pointer to a fixed buffer for reading the data into</param>
        /// <param name="count">The number of bytes to read</param>
        /// <param name="reader">The binary reader for the data</param>
        /// <returns>The number of bytes actually read</returns>
        private static int FRead(byte* ptr, int count, BinaryReader reader)
        {
            Span<byte> buffer = new Span<byte>(ptr, count);
            int numToRead = (int)
                Math.Min(count, reader.BaseStream.Length - reader.BaseStream.Position);
            for (int i = 0; i < numToRead; i++)
            {
                buffer[i] = reader.ReadByte();
            }
            return numToRead;
        }

        /// <summary>
        /// Writes data from a fixed byte array
        /// </summary>
        /// <param name="ptr">Pointer to a fixed byte array containing the data to write</param>
        /// <param name="count">The number of bytes in the array</param>
        /// <param name="writer">The binary writer used to write the data</param>
        /// <returns>The number of bytes actually written</returns>
        private static int FWrite(byte* ptr, int count, BinaryWriter writer)
        {
            Span<byte> data = new Span<byte>(ptr, count);
            writer.Write(data.ToArray(), 0, count);
            return count;
        }

        /// <summary>
        /// Writes data from a fixed byte array
        /// </summary>
        /// <param name="ptr">Pointer to a fixed byte array containing the data to write</param>
        /// <param name="count">The number of bytes in the array</param>
        /// <param name="writer">The binary writer used to write the data</param>
        /// <returns>The number of bytes written</returns>
        private static CLong FWrite(byte* ptr, CLong count, BinaryWriter writer)
        {
            int result = FWrite(ptr, (int)count.Value, writer);
            return new CLong(result);
        }

        private static bool CopyHeaders(
            BinaryReader fi,
            ogg_sync_state* si,
            ogg_stream_state* @is,
            BinaryWriter fo,
            ogg_sync_state* so,
            ogg_stream_state* os,
            vorbis_info* vi
        )
        {
            byte* buffer = ogg_sync_buffer(si, new CLong(4096));
            int numread = FRead(buffer, 4096, fi);
            ogg_sync_wrote(si, new CLong(numread));

            ogg_page page = new ogg_page();

            if (ogg_sync_pageout(si, &page) != 1)
            {
                return false;
            }

            ogg_stream_init(@is, ogg_page_serialno(&page));
            ogg_stream_init(os, ogg_page_serialno(&page));

            if (ogg_stream_pagein(@is, &page) < 0)
            {
                ogg_stream_clear(@is);
                ogg_stream_clear(os);
                return false;
            }

            ogg_packet packet = new ogg_packet();

            if (ogg_stream_packetout(@is, &packet) != 1)
            {
                ogg_stream_clear(@is);
                ogg_stream_clear(os);
                return false;
            }

            vorbis_comment vc = new vorbis_comment();
            vorbis_comment_init(&vc);

            if (vorbis_synthesis_headerin(vi, &vc, &packet) < 0)
            {
                vorbis_comment_clear(&vc);
                ogg_stream_clear(@is);
                ogg_stream_clear(os);
                return false;
            }

            ogg_stream_packetin(os, &packet);

            int i = 0;

            while (i < 2)
            {
                int res = ogg_sync_pageout(si, &page);

                if (res == 0)
                {
                    buffer = ogg_sync_buffer(si, new CLong(4096));
                    numread = FRead(buffer, 4096, fi);

                    if (numread == 0 && i < 2)
                    {
                        ogg_stream_clear(@is);
                        ogg_stream_clear(os);
                        return false;
                    }

                    ogg_sync_wrote(si, new CLong(4096));
                    continue;
                }

                if (res == 1)
                {
                    ogg_stream_pagein(@is, &page);

                    while (i < 2)
                    {
                        res = ogg_stream_packetout(@is, &packet);

                        if (res == 0)
                        {
                            break;
                        }
                        else if (res < 0)
                        {
                            vorbis_comment_clear(&vc);
                            ogg_stream_clear(@is);
                            ogg_stream_clear(os);
                            return false;
                        }

                        vorbis_synthesis_headerin(vi, &vc, &packet);
                        ogg_stream_packetin(os, &packet);
                        i++;
                    }
                }
            }

            vorbis_comment_clear(&vc);

            while (ogg_stream_flush(os, &page) != 0)
            {
                nint headerLengthWritten = FWrite(page.header, page.header_len, fo).Value;
                nint headerLengthActual = page.header_len.Value;
                nint bodyLengthWritten = FWrite(page.body, page.body_len, fo).Value;
                nint bodyLengthActual = page.body_len.Value;

                if (
                    headerLengthWritten != headerLengthActual
                    || bodyLengthWritten != bodyLengthActual
                )
                {
                    ogg_stream_clear(@is);
                    ogg_stream_clear(os);
                    return false;
                }
            }

            return true;
        }
    }
}
