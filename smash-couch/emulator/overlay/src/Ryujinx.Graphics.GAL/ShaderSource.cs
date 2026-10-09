using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.GAL
{
    public readonly struct ShaderSource
    {
        public string Code { get; }
        public byte[] BinaryCode { get; }
        public ShaderStage Stage { get; }
        public TargetLanguage Language { get; }
        public MslNumericVaryingFacts NumericVaryings { get; }

        public ShaderSource(string code, byte[] binaryCode, ShaderStage stage, TargetLanguage language)
            : this(code, binaryCode, stage, language, null)
        {
        }

        public ShaderSource(string code, byte[] binaryCode, ShaderStage stage, TargetLanguage language,
            MslNumericVaryingFacts numericVaryings)
        {
            Code = code;
            BinaryCode = binaryCode;
            Stage = stage;
            Language = language;
            NumericVaryings = numericVaryings;
        }

        public ShaderSource(string code, ShaderStage stage, TargetLanguage language) : this(code, null, stage, language)
        {
        }

        public ShaderSource(byte[] binaryCode, ShaderStage stage, TargetLanguage language) : this(null, binaryCode, stage, language)
        {
        }
    }
}
