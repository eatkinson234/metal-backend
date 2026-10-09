using Ryujinx.Graphics.GAL;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal
{
    class FormatConverter
    {
        public static void ConvertD24S8ToD32FS8(Span<byte> output, ReadOnlySpan<byte> input, Format format = Format.D24UnormS8Uint)
        {
            ValidatePackedFormat(format);
            if (input.Length % 4 != 0 || output.Length < checked(input.Length * 2))
                throw new ArgumentException("Invalid packed depth/stencil conversion span.");
            const float UnormToFloat = 1f / 0xffffff;

            Span<uint> outputUint = MemoryMarshal.Cast<byte, uint>(output);
            ReadOnlySpan<uint> inputUint = MemoryMarshal.Cast<byte, uint>(input);

            int i = 0;

            for (; i < inputUint.Length; i++)
            {
                uint depthStencil = inputUint[i];
                uint depth = format == Format.D24UnormS8Uint ? depthStencil >> 8 : depthStencil & 0xffffff;
                uint stencil = format == Format.D24UnormS8Uint ? depthStencil & 0xff :
                    format == Format.S8UintD24Unorm ? depthStencil >> 24 : 0;

                int j = i * 2;

                outputUint[j] = (uint)BitConverter.SingleToInt32Bits(depth * UnormToFloat);
                outputUint[j + 1] = stencil;
            }
        }

        public static void ConvertD32FS8ToD24S8(Span<byte> output, ReadOnlySpan<byte> input, Format format = Format.D24UnormS8Uint)
        {
            ValidatePackedFormat(format);
            if (input.Length % 8 != 0 || output.Length < input.Length / 2)
                throw new ArgumentException("Invalid host depth/stencil conversion span.");
            Span<uint> outputUint = MemoryMarshal.Cast<byte, uint>(output);
            ReadOnlySpan<uint> inputUint = MemoryMarshal.Cast<byte, uint>(input);

            int i = 0;

            for (; i < inputUint.Length; i += 2)
            {
                float depth = BitConverter.Int32BitsToSingle((int)inputUint[i]);
                uint stencil = inputUint[i + 1];
                uint depth24 = (uint)(Math.Clamp(depth, 0f, 1f) * 0xffffff);
                uint depthStencil = format == Format.D24UnormS8Uint ? (depth24 << 8) | (stencil & 0xff) :
                    format == Format.S8UintD24Unorm ? depth24 | ((stencil & 0xff) << 24) : depth24;

                int j = i >> 1;

                outputUint[j] = depthStencil;
            }
        }

        private static void ValidatePackedFormat(Format format)
        {
            if (format != Format.D24UnormS8Uint && format != Format.S8UintD24Unorm && format != Format.X8UintD24Unorm)
                throw new ArgumentException("Expected a packed D24 format.", nameof(format));
        }
    }
}
