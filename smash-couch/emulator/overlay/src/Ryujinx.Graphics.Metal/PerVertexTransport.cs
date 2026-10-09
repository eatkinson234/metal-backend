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
    // Native triangle-only compatibility transport. Finite components retain
    // hardware interpolation; exceptional classes use the documented local
    // policy. This does not assert NVIDIA-equivalent NaN interpolation.
    internal static class PerVertexTransport
    {
        private const string Prefix = "couch_transport_v1_";
        private const string PointPrefix = "couch_point_v1_";
        private const int ComponentLimit = 124;
        private readonly record struct Token(string Text, int Start, int End);
        private readonly record struct Edit(int Start, int End, string Text);
        internal static string Sha(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

        internal static ShaderSource[] Adapt(ShaderSource[] sources, out bool adapted, out string receipt)
        {
            adapted = false;
            receipt = null;
            if (sources.Length != 2 || sources.Any(s => s.Language != TargetLanguage.Msl || s.Code == null)) return sources;
            int vi = Array.FindIndex(sources, s => s.Stage == ShaderStage.Vertex);
            int fi = Array.FindIndex(sources, s => s.Stage == ShaderStage.Fragment);
            if (vi < 0 || fi < 0) return sources;
            // The immutable compiler metadata authenticates the original source,
            // but its old numeric-component selection is deliberately NOT used.
            if (sources[vi].NumericVaryings == null || sources[fi].NumericVaryings == null) return sources;
            if (!sources[vi].NumericVaryings.Matches(sources[vi].Code, ShaderStage.Vertex) ||
                !sources[fi].NumericVaryings.Matches(sources[fi].Code, ShaderStage.Fragment))
                throw new InvalidOperationException("Stale source-bound Metal transport metadata.");
            var rewritten = RewritePair(sources[vi].Code, sources[fi].Code, out int fields, out int budget, out int exits);
            if (fields == 0) return sources;
            var result = (ShaderSource[])sources.Clone();
            result[vi] = new ShaderSource(rewritten.Vertex, sources[vi].BinaryCode, ShaderStage.Vertex, TargetLanguage.Msl);
            result[fi] = new ShaderSource(rewritten.Fragment, sources[fi].BinaryCode, ShaderStage.Fragment, TargetLanguage.Msl);
            adapted = true;
            receipt = $"PerVertexTransport:v1 triangle-required fields={fields} components={budget}/{ComponentLimit} " +
                $"originalVertex={Sha(sources[vi].Code)} originalFragment={Sha(sources[fi].Code)} " +
                $"adaptedVertex={Sha(rewritten.Vertex)} adaptedFragment={Sha(rewritten.Fragment)} exits={exits} " +
                "method=linear-barycentric/raw-clipW finite=hardware exceptional=explicit-NaN-Inf-policy math=Safe/Precise language=MSL4";
            return result;
        }

        // Point-only raw-bit transport. Unlike the triangle adapter this does
        // not synthesize clip-W, barycentric weights, or interpolation helpers.
        internal static ShaderSource[] AdaptPoint(ShaderSource[] sources, out bool adapted, out string receipt)
        {
            adapted = false;
            receipt = null;
            if (sources.Length != 2 || sources.Any(s => s.Language != TargetLanguage.Msl || s.Code == null)) return sources;
            int vi = Array.FindIndex(sources, s => s.Stage == ShaderStage.Vertex);
            int fi = Array.FindIndex(sources, s => s.Stage == ShaderStage.Fragment);
            if (vi < 0 || fi < 0) return sources;
            if (sources[vi].NumericVaryings == null || sources[fi].NumericVaryings == null) return sources;
            if (!sources[vi].NumericVaryings.Matches(sources[vi].Code, ShaderStage.Vertex) ||
                !sources[fi].NumericVaryings.Matches(sources[fi].Code, ShaderStage.Fragment))
                throw new InvalidOperationException("Stale source-bound Metal point transport metadata.");
            var rewritten = RewritePointPair(sources[vi].Code, sources[fi].Code,
                out int fields, out int budget, out int exits);
            if (fields == 0) return sources;
            var result = (ShaderSource[])sources.Clone();
            result[vi] = new ShaderSource(rewritten.Vertex, sources[vi].BinaryCode, ShaderStage.Vertex, TargetLanguage.Msl);
            result[fi] = new ShaderSource(rewritten.Fragment, sources[fi].BinaryCode, ShaderStage.Fragment, TargetLanguage.Msl);
            adapted = true;
            receipt = $"PerVertexTransport:v1 point-required fields={fields} fragmentComponents={budget}/{ComponentLimit} " +
                $"originalVertex={Sha(sources[vi].Code)} originalFragment={Sha(sources[fi].Code)} " +
                $"adaptedVertex={Sha(rewritten.Vertex)} adaptedFragment={Sha(rewritten.Fragment)} exits={exits} " +
                "method=single-point-flat-raw-bit-copy finite=ordinary exceptional=zeroed-only-on-unused-varying raw=exact-no-arithmetic language=MSL4";
            return result;
        }

        // CPU regression seam; production additionally authenticates compiler sources.
        internal static (string Vertex, string Fragment) RewritePair(string vertex, string fragment,
            out int fieldCount, out int budget, out int exitCount)
        {
            if (vertex == null || fragment == null || vertex.Length > 4_194_304 || fragment.Length > 4_194_304 ||
                vertex.Contains(Prefix, StringComparison.Ordinal) || fragment.Contains(Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Unsupported or already adapted transport source.");
            List<Token> vt = Lex(vertex), ft = Lex(fragment);
            var vs = Struct(vt, "VertexOut"); var fs = Struct(ft, "FragmentIn");
            var vf = Fields(vt, vs, "outAttr", true, out int vertexComponents);
            var ff = Fields(ft, fs, "inAttr", false, out int baseComponents);
            foreach (int location in ff.Keys)
                if (!vf.ContainsKey(location)) throw new InvalidOperationException("Transport fragment field has no matching vertex field.");
            fieldCount = ff.Count;
            budget = checked(baseComponents + fieldCount * 4 + 4); // raw fields + xyz linear weights + scalar clipW
            exitCount = 0;
            if (fieldCount == 0) return (vertex, fragment);
            int vertexBudget = checked(vertexComponents + fieldCount * 4 + 1);
            if (vertexBudget > ComponentLimit) throw new InvalidOperationException("Transport exceeds conservative Metal vertex output component budget.");
            if (budget > ComponentLimit) throw new InvalidOperationException("Transport exceeds conservative Metal fragment input component budget.");
            int ve = Unique(vt, ["vertex", "VertexOut", "vertexMain", "("]);
            int va = Match(vt, ve + 3, "(", ")");
            int fe = Unique(ft, ["fragment", "FragmentOut", "fragmentMain", "("]);
            int fa = Match(ft, fe + 3, "(", ")");
            if (vt[va + 1].Text != "{" || ft[fa + 1].Text != "{" || vs.Close >= ve || fs.Close >= fe)
                throw new InvalidOperationException("Unsupported transport entrypoint layout.");
            int vc = Match(vt, va + 1, "{", "}");
            // Verify the fragment parameter before inserting assignments to it.
            string[] argument = ["FragmentIn", "in", "[", "[", "stage_in", "]", "]"];
            if (!argument.Select((t,i) => ft[fe + 4 + i].Text == t).All(x => x))
                throw new InvalidOperationException("Unsupported transport fragment stage-in parameter.");
            var vedits = new List<Edit>(); var fedits = new List<Edit>();
            // Point size has no triangle raster meaning. Keep the field and
            // every original computation, but remove the point-only builtin
            // from this triangle-guarded interface. The fragment does not use it.
            int point = Unique(vt, ["float", "point_size", "[", "[", "point_size", "]", "]", ";"]);
            vedits.Add(new Edit(vt[point+4].Start, vt[point+4].End,
                "user(couch_transport_v1_ignored_point_size)"));
            for (int i = va + 2; i < vc; i++)
            {
                if (vt[i].Text != "return") continue;
                if (i + 2 >= vc || vt[i+1].Text != "out" || vt[i+2].Text != ";")
                    throw new InvalidOperationException("Unsupported transport vertex return; refusing partial rewrite.");
                vedits.Add(new Edit(vt[i].Start, vt[i+2].End, "return " + Prefix + "exit(out);"));
                exitCount++;
                i += 2;
            }
            if (exitCount == 0) throw new InvalidOperationException("Transport vertex has no generated exits.");
            var vfields = new StringBuilder(); var ffields = new StringBuilder();
            vfields.Append("    uint ").Append(Prefix).Append("clipW [[user(couch_clipW)]];\n");
            ffields.Append("    vertex_value<uint> ").Append(Prefix).Append("clipW [[user(couch_clipW)]];\n")
                .Append("    float3 ").Append(Prefix).Append("linear [[barycentric_coord, center_no_perspective]];\n");
            var exit = new StringBuilder("\ninline VertexOut " + Prefix + "exit(VertexOut value) {\n");
            exit.Append("    value.").Append(Prefix).Append("clipW=as_type<uint>(value.position.w);\n");
            var entry = new StringBuilder("\n    float3 " + Prefix + "w=float3(as_type<float>(in." + Prefix + "clipW.get(vertex_index::first)), " +
                "as_type<float>(in." + Prefix + "clipW.get(vertex_index::second)), as_type<float>(in." + Prefix + "clipW.get(vertex_index::third)));\n" +
                "    float3 " + Prefix + "weights=" + Prefix + "perspective(in." + Prefix + "linear, " + Prefix + "w);\n");
            foreach (int location in ff.Keys)
            {
                string raw = Prefix + "raw" + location;
                vfields.Append($"    uint4 {raw} [[user(couch_raw{location})]];\n");
                ffields.Append($"    vertex_value<uint4> {raw} [[user(couch_raw{location})]];\n");
                exit.Append($"    value.{raw}=as_type<uint4>(value.outAttr{location});\n")
                    .Append($"    value.outAttr{location}=as_type<float4>(select(uint4(0u), value.{raw}, (value.{raw}&uint4(0x7f800000u))!=uint4(0x7f800000u)));\n");
                entry.Append($"    in.inAttr{location}={Prefix}recover4(in.{raw}.get(vertex_index::first), in.{raw}.get(vertex_index::second), in.{raw}.get(vertex_index::third), in.inAttr{location}, {Prefix}weights);\n");
            }
            exit.Append("    return value;\n}\n");
            vedits.Add(new Edit(vt[vs.Close].Start, vt[vs.Close].Start, vfields.ToString()));
            vedits.Add(new Edit(vt[vs.Close+1].End, vt[vs.Close+1].End, exit.ToString()));
            fedits.Add(new Edit(ft[fs.Close].Start, ft[fs.Close].Start, ffields.ToString()));
            fedits.Add(new Edit(ft[fs.Close+1].End, ft[fs.Close+1].End, FragmentHelpers));
            fedits.Add(new Edit(ft[fa+1].End, ft[fa+1].End, entry.ToString()));
            fedits.Add(new Edit(0, 0, "#include <metal_stdlib>\n#include <metal_vertex_value>\n"));
            return (Apply(vertex, vedits), Apply(fragment, fedits));
        }

        // CPU regression seam for point topology. One point has one provoking
        // vertex, so the integer payload is transported flat and restored by a
        // bit-cast. The ordinary float varying remains untouched for finite
        // values and is zeroed only for exceptional values Metal cannot carry.
        internal static (string Vertex, string Fragment) RewritePointPair(string vertex, string fragment,
            out int fieldCount, out int budget, out int exitCount)
        {
            if (vertex == null || fragment == null || vertex.Length > 4_194_304 || fragment.Length > 4_194_304 ||
                vertex.Contains(Prefix, StringComparison.Ordinal) || fragment.Contains(Prefix, StringComparison.Ordinal) ||
                vertex.Contains(PointPrefix, StringComparison.Ordinal) || fragment.Contains(PointPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Unsupported or already adapted point transport source.");

            List<Token> vt = Lex(vertex), ft = Lex(fragment);
            var vs = Struct(vt, "VertexOut");
            var fs = Struct(ft, "FragmentIn");
            var vf = Fields(vt, vs, "outAttr", true, out int vertexComponents);
            var ff = Fields(ft, fs, "inAttr", false, out int fragmentComponents);
            foreach (int location in ff.Keys)
                if (!vf.ContainsKey(location))
                    throw new InvalidOperationException("Point transport fragment field has no matching vertex field.");

            fieldCount = ff.Count;
            budget = checked(fragmentComponents + fieldCount * 4);
            exitCount = 0;
            if (fieldCount == 0) return (vertex, fragment);
            int pointSize = Unique(vt, ["float", "point_size", "[", "[", "point_size", "]", "]", ";"]);
            if (pointSize <= vs.Open || pointSize >= vs.Close)
                throw new InvalidOperationException("Point transport requires exactly one generated point_size builtin.");
            int vertexBudget = checked(vertexComponents + fieldCount * 4);
            if (vertexBudget > ComponentLimit)
                throw new InvalidOperationException("Point transport exceeds conservative Metal vertex output component budget.");
            if (budget > ComponentLimit)
                throw new InvalidOperationException("Point transport exceeds conservative Metal fragment input component budget.");

            int ve = Unique(vt, ["vertex", "VertexOut", "vertexMain", "("]);
            int va = Match(vt, ve + 3, "(", ")");
            int fe = Unique(ft, ["fragment", "FragmentOut", "fragmentMain", "("]);
            int fa = Match(ft, fe + 3, "(", ")");
            if (vt[va + 1].Text != "{" || ft[fa + 1].Text != "{" || vs.Close >= ve || fs.Close >= fe)
                throw new InvalidOperationException("Unsupported point transport entrypoint layout.");
            int vc = Match(vt, va + 1, "{", "}");
            string[] argument = ["FragmentIn", "in", "[", "[", "stage_in", "]", "]"];
            int argumentStart = fe + 4;
            int argumentEnd = argumentStart + argument.Length;
            if (argumentEnd >= ft.Count ||
                (ft[argumentEnd].Text != "," && ft[argumentEnd].Text != ")") ||
                (ft[argumentEnd].Text == ")" && fa != argumentEnd) ||
                !argument.Select((t, i) => ft[argumentStart + i].Text == t).All(x => x))
                throw new InvalidOperationException("Unsupported point transport fragment stage-in parameter.");

            var vedits = new List<Edit>();
            var fedits = new List<Edit>();
            for (int i = va + 2; i < vc; i++)
            {
                if (vt[i].Text != "return") continue;
                if (i + 2 >= vc || vt[i + 1].Text != "out" || vt[i + 2].Text != ";")
                    throw new InvalidOperationException("Unsupported point transport vertex return; refusing partial rewrite.");
                vedits.Add(new Edit(vt[i].Start, vt[i + 2].End, "return " + PointPrefix + "exit(out);"));
                exitCount++;
                i += 2;
            }
            if (exitCount == 0)
                throw new InvalidOperationException("Point transport vertex has no generated exits.");

            var vfields = new StringBuilder();
            var ffields = new StringBuilder();
            var exit = new StringBuilder("\ninline VertexOut " + PointPrefix + "exit(VertexOut value) {\n");
            var entry = new StringBuilder();
            foreach (int location in ff.Keys)
            {
                string raw = PointPrefix + "raw" + location;
                vfields.Append($"    uint4 {raw} [[user(couch_point_v1_raw{location})]];\n");
                ffields.Append($"    uint4 {raw} [[user(couch_point_v1_raw{location}), flat]];\n");
                exit.Append($"    value.{raw}=as_type<uint4>(value.outAttr{location});\n")
                    .Append($"    value.outAttr{location}=as_type<float4>(select(uint4(0u), value.{raw}, (value.{raw}&uint4(0x7f800000u))!=uint4(0x7f800000u)));\n");
                entry.Append($"\n    in.inAttr{location}=as_type<float4>(in.{raw});\n");
            }
            exit.Append("    return value;\n}\n");
            vedits.Add(new Edit(vt[vs.Close].Start, vt[vs.Close].Start, vfields.ToString()));
            vedits.Add(new Edit(vt[vs.Close + 1].End, vt[vs.Close + 1].End, exit.ToString()));
            fedits.Add(new Edit(ft[fs.Close].Start, ft[fs.Close].Start, ffields.ToString()));
            fedits.Add(new Edit(ft[fa + 1].End, ft[fa + 1].End, entry.ToString()));
            fedits.Add(new Edit(0, 0, "#include <metal_stdlib>\n"));
            return (Apply(vertex, vedits), Apply(fragment, fedits));
        }

        private static SortedDictionary<int,string> Fields(List<Token> tokens, (int Open,int Close) scope,
            string prefix, bool vertex, out int components)
        {
            var fields = new SortedDictionary<int,string>(); var builtins = new HashSet<string>(); components = 0;
            for (int i=scope.Open+1; i<scope.Close;)
            {
                int end=i; while (end<scope.Close && tokens[end].Text!=";") end++;
                if (end==scope.Close || end<i+2) throw new InvalidOperationException("Malformed transport interface declaration.");
                string type=tokens[i].Text, name=tokens[i+1].Text;
                if (name.StartsWith(prefix,StringComparison.Ordinal))
                {
                    if (!int.TryParse(name[prefix.Length..],out int location) || location<0 || location>=32 || fields.ContainsKey(location))
                        throw new InvalidOperationException("Invalid or duplicate transport location.");
                    VerifyField(tokens,scope.Open,scope.Close,name,"loc"+location);
                    fields.Add(location,name); components+=4;
                }
                else
                {
                    // Strict compiler-generated builtins only: no guessed arrays,
                    // additional user fields or implicit component budget.
                    string attribute; int width;
                    if (name=="position" && type=="float4") { attribute="position"; width=4; }
                    else if (vertex && name=="point_size" && type=="float") { attribute="point_size"; width=1; }
                    else if (!vertex && name=="front_facing" && type=="bool") { attribute="front_facing"; width=1; }
                    else if (!vertex && name=="point_coord" && type=="float2") { attribute="point_coord"; width=2; }
                    else if (!vertex && name=="primitive_id" && type=="uint") { attribute="primitive_id"; width=1; }
                    else throw new InvalidOperationException("Unknown transport interface field.");
                    string actual=string.Concat(tokens.Skip(i+2).Take(end-i-2).Select(t=>t.Text));
                    if (actual!="[["+attribute+"]]" && !(attribute=="position" && actual=="[[position,invariant]]"))
                        throw new InvalidOperationException("Unknown transport builtin qualifier.");
                    if (!builtins.Add(name)) throw new InvalidOperationException("Duplicate transport builtin.");
                    components+=width;
                }
                i=end+1;
            }
            if (!builtins.Contains("position")) throw new InvalidOperationException("Transport interface position is missing.");
            return fields;
        }

        private static string Apply(string source,List<Edit> edits)
        {
            var result=new StringBuilder(source);
            foreach (var edit in edits.OrderByDescending(e=>e.Start))
            { result.Remove(edit.Start,edit.End-edit.Start); result.Insert(edit.Start,edit.Text); }
            return result.ToString();
        }

        private const string FragmentHelpers = """

inline float3 couch_transport_v1_perspective(float3 linearWeights,float3 clipW) {
 #pragma METAL fp contract(off)
 float3 weighted=linearWeights/clipW;
 return weighted/((weighted.x+weighted.y)+weighted.z);
}
inline bool couch_transport_v1_nan(uint v) { return (v&0x7f800000u)==0x7f800000u && (v&0x007fffffu)!=0u; }
inline float couch_transport_v1_component(uint a,uint b,uint c,float ordinary,float3 weights) {
 #pragma METAL fp contract(off)
 if((a&0x7f800000u)!=0x7f800000u && (b&0x7f800000u)!=0x7f800000u && (c&0x7f800000u)!=0x7f800000u) return ordinary;
 if(couch_transport_v1_nan(a)||couch_transport_v1_nan(b)||couch_transport_v1_nan(c)) return as_type<float>(0x7fc00000u);
 float first=as_type<float>(a)*weights.x;
 float second=as_type<float>(b)*weights.y;
 float third=as_type<float>(c)*weights.z;
 float value=(first+second)+third;
 return isnan(value)?as_type<float>(0x7fc00000u):value;
}
inline float4 couch_transport_v1_recover4(uint4 a,uint4 b,uint4 c,float4 ordinary,float3 weights) {
 return float4(couch_transport_v1_component(a.x,b.x,c.x,ordinary.x,weights),
 couch_transport_v1_component(a.y,b.y,c.y,ordinary.y,weights),
 couch_transport_v1_component(a.z,b.z,c.z,ordinary.z,weights),
 couch_transport_v1_component(a.w,b.w,c.w,ordinary.w,weights));
}

""";
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
                bool matches = true;
                for (int n = 0; n < sequence.Length; n++)
                {
                    if (tokens[i + n].Text != sequence[n])
                    {
                        matches = false;
                        break;
                    }
                }
                if (!matches) continue;
                if (result != -1) throw new InvalidOperationException("Ambiguous generated shader structure.");
                result = i;
            }
            return result >= 0 ? result : throw new InvalidOperationException("Expected generated transport shader structure is absent.");
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
