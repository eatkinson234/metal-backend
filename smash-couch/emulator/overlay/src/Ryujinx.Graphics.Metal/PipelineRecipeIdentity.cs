using Ryujinx.Graphics.GAL;
using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Graphics.Metal;

internal static class PipelineRecipeIdentity
{
    internal static string Group(ShaderSource[] original, ResourceLayout layout, ComputeSize size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Text(hash, "MetalPipelineRecipeGroup/v1"); Sources(hash, original);
        Int(hash, size.X); Int(hash, size.Y); Int(hash, size.Z);
        Int(hash, layout.Sets.Count);
        foreach (var set in layout.Sets)
        {
            Int(hash, set.Descriptors.Count);
            foreach (var item in set.Descriptors)
            {
                Int(hash, item.Binding); Int(hash, item.Count); Int(hash, (int)item.Type); Int(hash, (int)item.Stages);
            }
        }
        Int(hash, layout.SetUsages.Count);
        foreach (var set in layout.SetUsages)
        {
            Int(hash, set.Usages.Count);
            foreach (var item in set.Usages)
            {
                Int(hash, item.Binding); Int(hash, item.ArrayLength); Int(hash, (int)item.Type);
                Int(hash, (int)item.Stages); Int(hash, item.Write ? 1 : 0);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string Program(string group, ShaderSource[] compiled, byte kind,
        ulong languageVersion, bool preserveInvariance, bool fastMath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Text(hash, "MetalPipelineRecipeProgram/v1"); Text(hash, group); Sources(hash, compiled);
        Int(hash, kind); ULong(hash, languageVersion); Int(hash, preserveInvariance ? 1 : 0); Int(hash, fastMath ? 1 : 0);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Sources(IncrementalHash hash, ShaderSource[] sources)
    {
        Int(hash, sources.Length);
        foreach (var source in sources)
        {
            Int(hash, (int)source.Stage); Int(hash, (int)source.Language); Text(hash, source.Code);
        }
    }
    private static void Text(IncrementalHash hash, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text); Int(hash, bytes.Length); hash.AppendData(bytes);
    }
    private static void Int(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); hash.AppendData(bytes);
    }
    private static void ULong(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); hash.AppendData(bytes);
    }
}
