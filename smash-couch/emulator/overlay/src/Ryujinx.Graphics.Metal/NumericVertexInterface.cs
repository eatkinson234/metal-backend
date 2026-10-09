using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    // This is an explicit host-domain compatibility choice, not a claim about
    // guest NaN colors. Metal forbids nonfinite numeric interpolants. Only exact
    // compiler-classified producer/consumer components are admitted. Guest math,
    // finite values, builtins and raw-bit/flat/unknown interfaces are unchanged.
    // The caller MUST compile with Safe/Precise (FastMathEnabled=false).
    internal static class NumericVertexInterface
    {
        private const string Helper = "couch_numeric_vertex_exit_v1";
        private readonly record struct Token(string Text, int Start, int End);
        private readonly record struct Edit(int Start, int End, string Text);
        internal static string Sha(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

        internal static ShaderSource[] Adapt(ShaderSource[] sources, out string receipt)
        {
            receipt = null;
            if (sources.Length != 2 || sources.Any(s => s.Language != TargetLanguage.Msl || s.Code == null)) return sources;
            int vertex = Array.FindIndex(sources, s => s.Stage == ShaderStage.Vertex);
            int fragment = Array.FindIndex(sources, s => s.Stage == ShaderStage.Fragment);
            if (vertex < 0 || fragment < 0) return sources;
            MslNumericVaryingFacts producer = sources[vertex].NumericVaryings;
            MslNumericVaryingFacts consumer = sources[fragment].NumericVaryings;
            if (producer == null || consumer == null ||
                !producer.Matches(sources[vertex].Code, ShaderStage.Vertex) ||
                !consumer.Matches(sources[fragment].Code, ShaderStage.Fragment)) return sources;

            var selections = new SortedDictionary<int, string>();
            for (int location = 0; location < 32; location++)
            {
                int mask = producer.GetComponentMask(location) & consumer.GetComponentMask(location);
                if (mask != 0)
                    selections.Add(location, new string("xyzw".Where((_, index) => (mask & (1 << index)) != 0).ToArray()));
            }
            if (selections.Count == 0) return sources;
            string adapted = RewriteSelectedNumericComponents(sources[vertex].Code, sources[fragment].Code,
                selections, out int exits);
            ShaderSource[] result = (ShaderSource[])sources.Clone();
            // The original compiler facts do not describe rewritten code.
            result[vertex] = new ShaderSource(adapted, sources[vertex].BinaryCode, ShaderStage.Vertex, TargetLanguage.Msl);
            string components = string.Join(",", selections.Select(s => $"outAttr{s.Key}.{s.Value}"));
            receipt = $"NumericVertexInterface:v2 eligibility=typed-IR-components policy=nonfinite-to-positive-zero components={components} " +
                $"originalVertex={producer.SourceSha256} originalFragment={consumer.SourceSha256} adaptedVertex={Sha(adapted)} " +
                $"adaptedFragment={consumer.SourceSha256} entrypointExits={exits} math=Safe/Precise guestArithmetic=unchanged";
            return result;
        }

        // An internal CPU/native regression seam, not a runtime configuration or
        // broader admission rule. Production requires paired, source-bound compiler facts.
        internal static string RewriteKnownNumericPair(string vertex, string fragment, int location,
            string components, out int exitCount)
        {
            return RewriteSelectedNumericComponents(vertex, fragment,
                new SortedDictionary<int, string> { [location] = components }, out exitCount);
        }

        private static string RewriteSelectedNumericComponents(string vertex, string fragment,
            IReadOnlyDictionary<int, string> selections, out int exitCount)
        {
            if (selections.Count == 0 || selections.Any(s => s.Key < 0 || s.Key >= 32 || s.Value.Length == 0 ||
                    s.Value.Distinct().Count() != s.Value.Length || s.Value.Any(c => !"xyzw".Contains(c))) ||
                vertex.Contains(Helper, StringComparison.Ordinal) || fragment.Contains(Helper, StringComparison.Ordinal))
                throw new InvalidOperationException("Unsupported numeric interface policy or already adapted shader.");
            List<Token> vt = Lex(vertex), ft = Lex(fragment);
            (int vOpen, int vClose) = Struct(vt, "VertexOut");
            (int fOpen, int fClose) = Struct(ft, "FragmentIn");
            foreach (int location in selections.Keys)
            {
                VerifyField(vt, vOpen, vClose, "outAttr" + location, "loc" + location);
                VerifyField(ft, fOpen, fClose, "inAttr" + location, "loc" + location);
            }

            int entry = Unique(vt, ["vertex", "VertexOut", "vertexMain", "("]);
            int argsClose = Match(vt, entry + 3, "(", ")");
            if (argsClose + 1 >= vt.Count || vt[argsClose + 1].Text != "{")
                throw new InvalidOperationException("Unsupported vertex entrypoint declaration.");
            int mainOpen = argsClose + 1, mainClose = Match(vt, mainOpen, "{", "}");
            if (vClose >= entry) throw new InvalidOperationException("VertexOut must precede the vertex entrypoint.");
            var edits = new List<Edit>();
            for (int i = mainOpen + 1; i < mainClose; i++)
            {
                if (vt[i].Text != "return") continue;
                if (i + 2 >= mainClose || vt[i + 1].Text != "out" || vt[i + 2].Text != ";")
                    throw new InvalidOperationException("Unsupported vertex entrypoint return; no partial adaptation.");
                edits.Add(new Edit(vt[i].Start, vt[i + 2].End, "return " + Helper + "(out);"));
                i += 2;
            }
            exitCount = edits.Count;
            if (exitCount == 0) throw new InvalidOperationException("No explicit generated vertex exits to adapt.");

            var helper = new StringBuilder("\n\n// Couch numeric interface v1: only an inspected undefined host domain is canonicalized.\n");
            helper.Append("inline VertexOut ").Append(Helper).Append("(VertexOut value)\n{\n");
            foreach (var selection in selections)
            {
                foreach (char component in selection.Value)
                {
                    string field = $"value.outAttr{selection.Key}.{component}";
                    helper.Append("    ").Append(field).Append(" = isfinite(").Append(field).Append(") ? ")
                        .Append(field).Append(" : 0.0f;\n");
                }
            }
            helper.Append("    return value;\n}\n");
            edits.Add(new Edit(vt[vClose + 1].End, vt[vClose + 1].End, helper.ToString()));
            var result = new StringBuilder(vertex);
            foreach (Edit edit in edits.OrderByDescending(e => e.Start))
            {
                result.Remove(edit.Start, edit.End - edit.Start);
                result.Insert(edit.Start, edit.Text);
            }
            return result.ToString();
        }

        private static (int Open, int Close) Struct(List<Token> tokens, string name)
        {
            int start = Unique(tokens, ["struct", name, "{"]);
            int close = Match(tokens, start + 2, "{", "}");
            if (close + 1 >= tokens.Count || tokens[close + 1].Text != ";")
                throw new InvalidOperationException("Unsupported interface struct terminator.");
            return (start + 2, close);
        }

        private static void VerifyField(List<Token> tokens, int open, int close, string name, string location)
        {
            string[] declaration = ["float4", name, "[", "[", "user", "(", location, ")", "]", "]", ";"];
            int matches = 0;
            for (int i = open + 1; i < close; i++)
            {
                if (tokens[i].Text != name) continue;
                if (i < 2 || (tokens[i - 2].Text != "{" && tokens[i - 2].Text != ";") ||
                    i + declaration.Length - 1 > close ||
                    !declaration.Select((v, n) => tokens[i - 1 + n].Text == v).All(v => v))
                    throw new InvalidOperationException("Unknown, non-float, flat or qualified numeric interface field.");
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("Numeric interface field is missing or duplicated.");
        }

        private static int Unique(List<Token> tokens, string[] sequence)
        {
            int result = -1;
            for (int i = 0; i <= tokens.Count - sequence.Length; i++)
            {
                if (!sequence.Select((v, n) => tokens[i + n].Text == v).All(v => v)) continue;
                if (result != -1) throw new InvalidOperationException("Ambiguous generated shader structure.");
                result = i;
            }
            return result >= 0 ? result : throw new InvalidOperationException("Expected generated shader structure is absent.");
        }

        private static int Match(List<Token> tokens, int open, string left, string right)
        {
            if (tokens[open].Text != left) throw new InvalidOperationException("Invalid shader scope opening.");
            int depth = 0;
            for (int i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Text == left) depth++;
                if (tokens[i].Text == right && --depth == 0) return i;
            }
            throw new InvalidOperationException("Unbalanced generated shader scope.");
        }

        private static List<Token> Lex(string source)
        {
            var result = new List<Token>();
            for (int i = 0; i < source.Length;)
            {
                if (char.IsWhiteSpace(source[i])) { i++; continue; }
                if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
                {
                    int end = source.IndexOf('\n', i + 2); i = end < 0 ? source.Length : end + 1; continue;
                }
                if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new InvalidOperationException("Unterminated shader comment.");
                    i = end + 2; continue;
                }
                int start = i++;
                if (source[start] == '"' || source[start] == '\'')
                {
                    char quote = source[start]; bool ended = false;
                    while (i < source.Length)
                    {
                        char next = source[i++];
                        if (next == '\\') { if (i < source.Length) i++; continue; }
                        if (next == quote) { ended = true; break; }
                    }
                    if (!ended) throw new InvalidOperationException("Unterminated shader literal.");
                }
                else if (char.IsLetterOrDigit(source[start]) || source[start] == '_')
                {
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                }
                result.Add(new Token(source[start..i], start, i));
            }
            return result;
        }
    }
}
