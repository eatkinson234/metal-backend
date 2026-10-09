using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Graphics.Shader
{
    // Compiler-produced facts for one immutable generated source. This is not
    // permission to alter arbitrary float fields or the guest's arithmetic.
    public sealed class MslNumericVaryingFacts
    {
        public const int PolicyVersion = 1;
        private readonly byte[] _components;

        public ShaderStage Stage { get; }
        public string SourceSha256 { get; }
        public IReadOnlyDictionary<string, int> RefusalCounts { get; }

        internal MslNumericVaryingFacts(ShaderStage stage, string source, byte[] components,
            IReadOnlyDictionary<string, int> refusalCounts)
        {
            if (stage is not (ShaderStage.Vertex or ShaderStage.Fragment) || source == null ||
                components == null || components.Length != 32)
                throw new ArgumentException("Unsupported numerical varying facts.");
            foreach (byte mask in components)
                if ((mask & 0xf0) != 0) throw new ArgumentException("Invalid component mask.");
            Stage = stage;
            SourceSha256 = Hash(source);
            _components = (byte[])components.Clone();
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            if (refusalCounts != null)
                foreach (var item in refusalCounts) reasons.Add(item.Key, item.Value);
            RefusalCounts = new ReadOnlyDictionary<string, int>(reasons);
        }

        public byte GetComponentMask(int location)
        {
            if ((uint)location >= 32) throw new ArgumentOutOfRangeException(nameof(location));
            return _components[location];
        }

        public bool Matches(string source, ShaderStage stage) =>
            source != null && Stage == stage && SourceSha256 == Hash(source);

        private static string Hash(string source) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}
