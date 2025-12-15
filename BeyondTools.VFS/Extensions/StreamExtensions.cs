using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeyondTools.VFS.Extensions
{
    internal static class StreamExtensions
    {
        public static void CopyBytes(this Stream inStream, Stream outStream, long? count = null)
        {
            if (count == null)
            {
                inStream.CopyTo(outStream);
                return;
            }

            long readBytes = 0L;
            var buffer = new byte[64 * 1024];
            do
            {
                var toRead = Math.Min((long)(count - readBytes), buffer.LongLength);
                var readNow = inStream.Read(buffer, 0, (int)toRead);
                if (readNow == 0)
                    break;
                outStream.Write(buffer, 0, readNow);
                readBytes += readNow;
            } while (readBytes < count);
        }
    }
}
