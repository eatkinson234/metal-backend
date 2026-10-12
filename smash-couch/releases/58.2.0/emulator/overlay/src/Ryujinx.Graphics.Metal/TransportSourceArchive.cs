using Ryujinx.Graphics.GAL;
using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    // The original sources of a per-vertex transport program, kept so a point
    // variant can be built if the program is ever drawn with points
    // (Program.ResolveForPrimitive). Nearly every program uses transport and
    // point variants are rare (4 in a 40-minute tour), but as UTF-16 strings
    // the sources average roughly 190 KB per program: about 3 GB of the
    // launcher's 4 GiB managed heap with a 16k-program cache, and rig tour6
    // ended in an OutOfMemoryException. Once the program has compiled,
    // Compact() keeps each source as deflated UTF-8 (about 8x smaller) and
    // Expand() rebuilds identical sources. Encoding is strict, so a source
    // that could not round-trip exactly is kept as it was.
    sealed class TransportSourceArchive
    {
        private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly object _gate = new();
        // Everything but the code: stage, language, binary and varying facts.
        private readonly ShaderSource[] _shells;
        // Per stage, exactly one of these holds the code (or neither, when the
        // stage had none).
        private readonly string[] _code;
        private readonly byte[][] _packed;

        internal TransportSourceArchive(ShaderSource[] sources)
        {
            _shells = new ShaderSource[sources.Length];
            _code = new string[sources.Length];
            _packed = new byte[sources.Length][];
            for (int index = 0; index < sources.Length; index++)
            {
                ShaderSource source = sources[index];
                _shells[index] = new ShaderSource(null, source.BinaryCode, source.Stage, source.Language, source.NumericVaryings);
                _code[index] = source.Code;
            }
        }

        // Runs once, on the compile worker after the program is published, so
        // it adds nothing to a render-thread wait. Concurrent Expand() calls
        // see either form; both decode to the same text.
        internal void Compact()
        {
            for (int index = 0; index < _code.Length; index++)
            {
                string code;
                lock (_gate)
                {
                    code = _code[index];
                }
                if (code == null)
                {
                    continue;
                }

                byte[] packed;
                try
                {
                    packed = Pack(code);
                }
                catch (Exception)
                {
                    // Not exactly representable (or out of memory): keep the string.
                    continue;
                }

                lock (_gate)
                {
                    _packed[index] = packed;
                    _code[index] = null;
                }
            }
        }

        internal ShaderSource[] Expand()
        {
            ShaderSource[] sources = new ShaderSource[_shells.Length];
            for (int index = 0; index < sources.Length; index++)
            {
                string code;
                byte[] packed;
                lock (_gate)
                {
                    code = _code[index];
                    packed = _packed[index];
                }
                if (code == null && packed != null)
                {
                    code = Unpack(packed);
                }
                ShaderSource shell = _shells[index];
                sources[index] = new ShaderSource(code, shell.BinaryCode, shell.Stage, shell.Language, shell.NumericVaryings);
            }
            return sources;
        }

        private static byte[] Pack(string code)
        {
            // Pooled scratch: one large array per source would be large-object
            // garbage for every program at boot.
            byte[] utf8 = ArrayPool<byte>.Shared.Rent(_strictUtf8.GetMaxByteCount(code.Length));
            try
            {
                int length = _strictUtf8.GetBytes(code, 0, code.Length, utf8, 0);
                using MemoryStream output = new(length / 4 + 256);
                using (DeflateStream deflate = new(output, CompressionLevel.Fastest, leaveOpen: true))
                {
                    deflate.Write(utf8, 0, length);
                }
                return output.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(utf8);
            }
        }

        private static string Unpack(byte[] packed)
        {
            using DeflateStream deflate = new(new MemoryStream(packed, writable: false), CompressionMode.Decompress);
            using MemoryStream utf8 = new();
            deflate.CopyTo(utf8);
            return _strictUtf8.GetString(utf8.GetBuffer(), 0, (int)utf8.Length);
        }
    }
}
