using Ryujinx.Graphics.Shader.Translation;
using System;

namespace Ryujinx.Graphics.Shader
{
    public class ShaderProgram
    {
        public ShaderProgramInfo Info { get; }
        public TargetLanguage Language { get; }

        public string Code { get; private set; }
        public byte[] BinaryCode { get; }
        public MslNumericVaryingFacts NumericVaryings { get; private set; }

        private ShaderProgram(ShaderProgramInfo info, TargetLanguage language)
        {
            Info = info;
            Language = language;
        }

        public ShaderProgram(ShaderProgramInfo info, TargetLanguage language, string code) : this(info, language)
        {
            Code = code;
        }

        public ShaderProgram(ShaderProgramInfo info, TargetLanguage language, string code,
            MslNumericVaryingFacts numericVaryings) : this(info, language, code)
        {
            if (numericVaryings != null &&
                (language != TargetLanguage.Msl || !numericVaryings.Matches(code, info.Stage)))
                throw new ArgumentException("Numerical varying facts do not match this source.");
            NumericVaryings = numericVaryings;
        }

        public ShaderProgram(ShaderProgramInfo info, TargetLanguage language, byte[] binaryCode) : this(info, language)
        {
            BinaryCode = binaryCode;
        }

        public void Prepend(string line)
        {
            Code = line + Environment.NewLine + Code;
            // Even diagnostic text creates a different source identity. A caller
            // cannot silently rebind facts to subsequently modified source.
            NumericVaryings = null;
        }
    }
}
