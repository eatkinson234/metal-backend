#include <metal_stdlib>
#include <metal_vertex_value>
#pragma clang diagnostic ignored "-Wunused-variable"

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct FragmentIn
{
    float4 position [[position, invariant]];
    bool front_facing [[front_facing]];
    float2 point_coord [[point_coord]];
    uint primitive_id [[primitive_id]];
    float4 inAttr0 [[user(loc0)]];
    float4 inAttr1 [[user(loc1)]];
    float4 inAttr2 [[user(loc2)]];
    float4 inAttr3 [[user(loc3)]];
    float4 inAttr4 [[user(loc4)]];
    float4 inAttr5 [[user(loc5)]];
    float4 inAttr6 [[user(loc6)]];
    vertex_value<uint> couch_transport_v1_clipW [[user(couch_clipW)]];
    float3 couch_transport_v1_linear [[barycentric_coord, center_no_perspective]];
    vertex_value<uint4> couch_transport_v1_raw0 [[user(couch_raw0)]];
    vertex_value<uint4> couch_transport_v1_raw1 [[user(couch_raw1)]];
    vertex_value<uint4> couch_transport_v1_raw2 [[user(couch_raw2)]];
    vertex_value<uint4> couch_transport_v1_raw3 [[user(couch_raw3)]];
    vertex_value<uint4> couch_transport_v1_raw4 [[user(couch_raw4)]];
    vertex_value<uint4> couch_transport_v1_raw5 [[user(couch_raw5)]];
    vertex_value<uint4> couch_transport_v1_raw6 [[user(couch_raw6)]];
};
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


struct FragmentOut
{
    float4 color0 [[color(0)]];
};

struct struct_support_buffer
{
    uint alpha_test;
    uint4 is_bgra[8];
    float4 viewport_inverse;
    float4 viewport_size;
    int frag_scale_count;
    float4 render_scale[73];
    int4 tfe_offset;
    int tfe_vertex_count;
};

struct struct_fp_c1
{
    float4 data[4096];
};

struct struct_fp_c3
{
    float4 data[4096];
};

struct struct_fp_c4
{
    float4 data[4096];
};

struct struct_fp_c9
{
    float4 data[4096];
};

struct struct_fp_c10
{
    float4 data[4096];
};

struct ConstantBuffers
{
    constant struct_support_buffer* support_buffer;
    constant struct_fp_c1* fp_c1;
    constant struct_fp_c3* fp_c3;
    constant struct_fp_c4* fp_c4;
    constant struct_fp_c9* fp_c9;
    constant struct_fp_c10* fp_c10;
};

struct StorageBuffers
{
};

struct Textures
{
    texture2d<float> tex_fp_t_tcb_E;
    sampler samp_fp_t_tcb_E;
    texture2d<float> tex_fp_t_tcb_8;
    sampler samp_fp_t_tcb_8;
};

fragment FragmentOut fragmentMain(FragmentIn in [[stage_in]], 
                                  constant ConstantBuffers &constant_buffers [[buffer(17)]], 
                                  device StorageBuffers &storage_buffers [[buffer(18)]], 
                                  constant Textures &textures [[buffer(19)]])
{
    float3 couch_transport_v1_w=float3(as_type<float>(in.couch_transport_v1_clipW.get(vertex_index::first)), as_type<float>(in.couch_transport_v1_clipW.get(vertex_index::second)), as_type<float>(in.couch_transport_v1_clipW.get(vertex_index::third)));
    float3 couch_transport_v1_weights=couch_transport_v1_perspective(in.couch_transport_v1_linear, couch_transport_v1_w);
    in.inAttr0=couch_transport_v1_recover4(in.couch_transport_v1_raw0.get(vertex_index::first), in.couch_transport_v1_raw0.get(vertex_index::second), in.couch_transport_v1_raw0.get(vertex_index::third), in.inAttr0, couch_transport_v1_weights);
    in.inAttr1=couch_transport_v1_recover4(in.couch_transport_v1_raw1.get(vertex_index::first), in.couch_transport_v1_raw1.get(vertex_index::second), in.couch_transport_v1_raw1.get(vertex_index::third), in.inAttr1, couch_transport_v1_weights);
    in.inAttr2=couch_transport_v1_recover4(in.couch_transport_v1_raw2.get(vertex_index::first), in.couch_transport_v1_raw2.get(vertex_index::second), in.couch_transport_v1_raw2.get(vertex_index::third), in.inAttr2, couch_transport_v1_weights);
    in.inAttr3=couch_transport_v1_recover4(in.couch_transport_v1_raw3.get(vertex_index::first), in.couch_transport_v1_raw3.get(vertex_index::second), in.couch_transport_v1_raw3.get(vertex_index::third), in.inAttr3, couch_transport_v1_weights);
    in.inAttr4=couch_transport_v1_recover4(in.couch_transport_v1_raw4.get(vertex_index::first), in.couch_transport_v1_raw4.get(vertex_index::second), in.couch_transport_v1_raw4.get(vertex_index::third), in.inAttr4, couch_transport_v1_weights);
    in.inAttr5=couch_transport_v1_recover4(in.couch_transport_v1_raw5.get(vertex_index::first), in.couch_transport_v1_raw5.get(vertex_index::second), in.couch_transport_v1_raw5.get(vertex_index::third), in.inAttr5, couch_transport_v1_weights);
    in.inAttr6=couch_transport_v1_recover4(in.couch_transport_v1_raw6.get(vertex_index::first), in.couch_transport_v1_raw6.get(vertex_index::second), in.couch_transport_v1_raw6.get(vertex_index::third), in.inAttr6, couch_transport_v1_weights);

    FragmentOut out = {};
    uint thread_index_in_simdgroup = simd_prefix_exclusive_sum(1);
    float temp_0;
    float temp_1;
    float temp_2;
    float temp_3;
    float temp_4;
    float temp_5;
    float temp_6;
    float temp_7;
    float temp_8;
    float temp_9;
    float temp_10;
    float temp_11;
    float temp_12;
    float temp_13;
    float temp_14;
    float temp_15;
    float temp_16;
    float temp_17;
    float temp_18;
    float temp_19;
    float temp_20;
    float temp_21;
    float temp_22;
    float temp_23;
    float temp_24;
    float temp_25;
    float temp_26;
    float temp_27;
    float temp_28;
    float temp_29;
    float temp_30;
    float temp_31;
    float temp_32;
    float temp_33;
    float temp_34;
    float temp_35;
    float temp_36;
    float temp_37;
    float temp_38;
    float temp_39;
    float temp_40;
    float temp_41;
    float temp_42;
    float temp_43;
    float temp_44;
    float temp_45;
    float temp_46;
    float temp_47;
    float temp_48;
    float temp_49;
    float temp_50;
    float temp_51;
    float temp_52;
    float temp_53;
    float temp_54;
    float temp_55;
    float temp_56;
    float temp_57;
    float temp_58;
    float temp_59;
    float temp_60;
    float temp_61;
    float temp_62;
    float temp_63;
    float temp_64;
    float temp_65;
    float temp_66;
    float temp_67;
    float temp_68;
    float temp_69;
    float temp_70;
    float temp_71;
    float temp_72;
    bool temp_73;
    float temp_74;
    float temp_75;
    float temp_76;
    float temp_77;
    float temp_78;
    bool temp_79;
    bool temp_80;
    float temp_81;
    float temp_82;
    bool temp_83;
    bool temp_84;
    bool temp_85;
    bool temp_86;
    bool temp_87;
    bool temp_88;
    float temp_89;
    float temp_90;
    float temp_91;
    float temp_92;
    float temp_93;
    float temp_94;
    float4 temp_95;
    float temp_96;
    float temp_97;
    float temp_98;
    float temp_99;
    float temp_100;
    float temp_101;
    float temp_102;
    float temp_103;
    float temp_104;
    float temp_105;
    bool temp_106;
    float temp_107;
    float temp_108;
    float temp_109;
    float temp_110;
    float temp_111;
    float temp_112;
    float temp_113;
    float temp_114;
    float temp_115;
    float temp_116;
    float temp_117;
    float temp_118;
    float temp_119;
    float temp_120;
    float temp_121;
    float temp_122;
    bool temp_123;
    float temp_124;
    float temp_125;
    float temp_126;
    float temp_127;
    float temp_128;
    bool temp_129;
    uint temp_130;
    float temp_131;
    float temp_132;
    float temp_133;
    float temp_134;
    float temp_135;
    float temp_136;
    float temp_137;
    float temp_138;
    float temp_139;
    // 0x000008: 0x4C98078C05770007 Mov
    // 0x000010: 0xE003FF87CFF7FF04 Ipa
    // 0x000018: 0x5080000000470404 Mufu
    // 0x000028: 0xE043FF88C047FF06 Ipa
    temp_0 = in.inAttr0.w;
    // 0x000030: 0xE043FF880047FF03 Ipa
    temp_1 = in.inAttr0.x;
    // 0x000038: 0xE043FF884047FF02 Ipa
    temp_2 = in.inAttr0.y;
    // 0x000048: 0x4C9807A80D170008 Mov
    // 0x000050: 0x4C68120C05670808 Fmul
    temp_3 = constant_buffers.fp_c10->data[52].y * 0.5;
    temp_4 = temp_3 * constant_buffers.fp_c3->data[21].z;
    // 0x000058: 0x5080000000470605 Mufu
    temp_5 = 1.0f / temp_0;
    // 0x000068: 0x5C68100000570303 Fmul
    temp_6 = temp_1 * temp_5;
    // 0x000070: 0x5C68100000570202 Fmul
    temp_7 = temp_2 * temp_5;
    // 0x000078: 0x3968103F00070705 Fmul
    temp_8 = constant_buffers.fp_c3->data[21].w * -0.5;
    // 0x000088: 0x3868103F00070707 Fmul
    temp_9 = constant_buffers.fp_c3->data[21].w * 0.5;
    // 0x000090: 0x59A003800027050A Ffma
    temp_10 = fma(temp_8, temp_7, temp_9);
    // 0x000098: 0x59A0040000870305 Ffma
    temp_11 = fma(temp_6, temp_4, temp_4);
    // 0x0000A8: 0xD82000EFF0A70505 Texs
    temp_12 = (is_null_texture(textures.tex_fp_t_tcb_E) ? decltype(textures.tex_fp_t_tcb_E.sample(textures.samp_fp_t_tcb_E, float2(temp_11, temp_10)).x)(0) : textures.tex_fp_t_tcb_E.sample(textures.samp_fp_t_tcb_E, float2(temp_11, temp_10)).x);
    // 0x0000B0: 0x4C68100C04370309 Fmul
    temp_13 = temp_6 * constant_buffers.fp_c3->data[16].w;
    // 0x0000B8: 0xE043FF8B0047FF0C Ipa
    temp_14 = in.inAttr3.x;
    // 0x0000C8: 0x4C68100C04270307 Fmul
    temp_15 = temp_6 * constant_buffers.fp_c3->data[16].z;
    // 0x0000D0: 0x4C68100C04170308 Fmul
    temp_16 = temp_6 * constant_buffers.fp_c3->data[16].y;
    // 0x0000D8: 0x4C68100C04070303 Fmul
    temp_17 = temp_6 * constant_buffers.fp_c3->data[16].x;
    // 0x0000E8: 0x49A0048C04770206 Ffma
    temp_18 = fma(temp_7, constant_buffers.fp_c3->data[17].w, temp_13);
    // 0x0000F0: 0xE043FF8E0047FF0A Ipa
    temp_19 = in.inAttr6.x;
    // 0x0000F8: 0x49A0038C04670207 Ffma
    temp_20 = fma(temp_7, constant_buffers.fp_c3->data[17].z, temp_15);
    // 0x000108: 0x49A0040C04570208 Ffma
    temp_21 = fma(temp_7, constant_buffers.fp_c3->data[17].y, temp_16);
    // 0x000110: 0x49A0018C0447020B Ffma
    temp_22 = fma(temp_7, constant_buffers.fp_c3->data[17].x, temp_17);
    // 0x000118: 0xE043FF8D0047FF03 Ipa
    temp_23 = in.inAttr5.x;
    // 0x000128: 0xE043FF8C0047FF02 Ipa
    temp_24 = in.inAttr4.x;
    // 0x000130: 0x49A0030C04B70506 Ffma
    temp_25 = fma(temp_12, constant_buffers.fp_c3->data[18].w, temp_18);
    // 0x000138: 0x49A0040C04970508 Ffma
    temp_26 = fma(temp_12, constant_buffers.fp_c3->data[18].y, temp_21);
    // 0x000148: 0x49A0058C04870509 Ffma
    temp_27 = fma(temp_12, constant_buffers.fp_c3->data[18].x, temp_22);
    // 0x000150: 0xE043FF8B4047FF0B Ipa
    temp_28 = in.inAttr3.y;
    // 0x000158: 0x4C58100C04F7060E Fadd
    temp_29 = temp_25 + constant_buffers.fp_c3->data[19].w;
    // 0x000168: 0x5080000000470E06 Mufu
    temp_30 = 1.0f / temp_29;
    // 0x000170: 0x49A0038C04A70505 Ffma
    temp_31 = fma(temp_12, constant_buffers.fp_c3->data[18].z, temp_20);
    // 0x000178: 0xE043FF8E4047FF07 Ipa
    temp_32 = in.inAttr6.y;
    // 0x000188: 0x4C58100C04D70808 Fadd
    temp_33 = temp_26 + constant_buffers.fp_c3->data[19].y;
    // 0x000190: 0x4C58100C04C7090D Fadd
    temp_34 = temp_27 + constant_buffers.fp_c3->data[19].x;
    // 0x000198: 0x4C58100C04E70509 Fadd
    temp_35 = temp_31 + constant_buffers.fp_c3->data[19].z;
    // 0x0001A8: 0x5C68100000670D05 Fmul
    temp_36 = temp_34 * temp_30;
    // 0x0001B0: 0x5C68100000670808 Fmul
    temp_37 = temp_33 * temp_30;
    // 0x0001B8: 0x5C68100000670906 Fmul
    temp_38 = temp_35 * temp_30;
    // 0x0001C8: 0xE043FF8D4047FF09 Ipa
    temp_39 = in.inAttr5.y;
    // 0x0001D0: 0x5C68100000C7050D Fmul
    temp_40 = temp_36 * temp_14;
    // 0x0001D8: 0xE043FF8D8047FF0C Ipa
    temp_41 = in.inAttr5.z;
    // 0x0001E8: 0x5C68100000A7050A Fmul
    temp_42 = temp_36 * temp_19;
    // 0x0001F0: 0x5C68100000370503 Fmul
    temp_43 = temp_36 * temp_23;
    // 0x0001F8: 0x5C68100000270502 Fmul
    temp_44 = temp_36 * temp_24;
    // 0x000208: 0xE043FF8C4047FF05 Ipa
    temp_45 = in.inAttr4.y;
    // 0x000210: 0x59A0068000B7080B Ffma
    temp_46 = fma(temp_37, temp_28, temp_40);
    // 0x000218: 0xE043FF8B8047FF0D Ipa
    temp_47 = in.inAttr3.z;
    // 0x000228: 0x59A0050000770807 Ffma
    temp_48 = fma(temp_37, temp_32, temp_42);
    // 0x000230: 0x59A0018000970809 Ffma
    temp_49 = fma(temp_37, temp_39, temp_43);
    // 0x000238: 0xE043FF8BC047FF03 Ipa
    temp_50 = in.inAttr3.w;
    // 0x000248: 0x59A0010000570808 Ffma
    temp_51 = fma(temp_37, temp_45, temp_44);
    // 0x000250: 0xE043FF8C8047FF05 Ipa
    temp_52 = in.inAttr4.z;
    // 0x000258: 0x59A0048000C7060C Ffma
    temp_53 = fma(temp_38, temp_41, temp_49);
    // 0x000268: 0xE043FF8CC047FF02 Ipa
    temp_54 = in.inAttr4.w;
    // 0x000270: 0x59A0058000D7060A Ffma
    temp_55 = fma(temp_38, temp_47, temp_46);
    // 0x000278: 0xE043FF8E8047FF0D Ipa
    temp_56 = in.inAttr6.z;
    // 0x000288: 0xE043FF8DC047FF09 Ipa
    temp_57 = in.inAttr5.w;
    // 0x000290: 0x5C58100000A70303 Fadd
    temp_58 = temp_50 + temp_55;
    // 0x000298: 0xE043FF8EC047FF0A Ipa
    temp_59 = in.inAttr6.w;
    // 0x0002A8: 0x59A0040000570605 Ffma
    temp_60 = fma(temp_38, temp_52, temp_51);
    // 0x0002B0: 0x5C58100000570202 Fadd
    temp_61 = temp_54 + temp_60;
    // 0x0002B8: 0x4C68101001170305 Fmul
    temp_62 = temp_58 * constant_buffers.fp_c4->data[4].y;
    // 0x0002C8: 0x59A0038000D70607 Ffma
    temp_63 = fma(temp_38, temp_56, temp_48);
    // 0x0002D0: 0x5C58100000C70909 Fadd
    temp_64 = temp_57 + temp_53;
    // 0x0002D8: 0x49A0029001570206 Ffma
    temp_65 = fma(temp_61, constant_buffers.fp_c4->data[5].y, temp_62);
    // 0x0002E8: 0x49A0031001970905 Ffma
    temp_66 = fma(temp_64, constant_buffers.fp_c4->data[6].y, temp_65);
    // 0x0002F0: 0x5C58100000770A0A Fadd
    temp_67 = temp_59 + temp_63;
    // 0x0002F8: 0x4C68101001070307 Fmul
    temp_68 = temp_58 * constant_buffers.fp_c4->data[4].x;
    // 0x000308: 0x49A0029001D70A05 Ffma
    temp_69 = fma(temp_67, constant_buffers.fp_c4->data[7].y, temp_66);
    // 0x000310: 0x49A0039001470207 Ffma
    temp_70 = fma(temp_61, constant_buffers.fp_c4->data[5].x, temp_68);
    // 0x000318: 0x4C68101001270303 Fmul
    temp_71 = temp_58 * constant_buffers.fp_c4->data[4].z;
    // 0x000328: 0x36B683BF00070587 Fsetp
    temp_72 = abs(temp_69);
    temp_73 = temp_72 >= 0.5;
    // 0x000330: 0x49A0039001870907 Ffma
    temp_74 = fma(temp_64, constant_buffers.fp_c4->data[6].x, temp_70);
    // 0x000338: 0x49A0019001670202 Ffma
    temp_75 = fma(temp_61, constant_buffers.fp_c4->data[5].z, temp_71);
    // 0x000348: 0x49A0039001C70A07 Ffma
    temp_76 = fma(temp_67, constant_buffers.fp_c4->data[7].x, temp_74);
    // 0x000350: 0x49A0011001A70909 Ffma
    temp_77 = fma(temp_64, constant_buffers.fp_c4->data[6].z, temp_75);
    // 0x000358: 0x36B6A03F00070787 Fsetp
    temp_78 = abs(temp_76);
    temp_79 = temp_78 >= 0.5;
    temp_80 = temp_79 || temp_73;
    // 0x000368: 0x49A0049001E70A09 Ffma
    temp_81 = fma(temp_67, constant_buffers.fp_c4->data[7].z, temp_77);
    // 0x000370: 0x36B6A03F00070987 Fsetp
    temp_82 = abs(temp_81);
    temp_83 = temp_82 >= 0.5;
    temp_84 = temp_83 || temp_80;
    // 0x000378: 0x50900380E000F00F Psetp
    temp_85 = (0 != 0);
    if (temp_84)
    {
        temp_85 = false;
    }
    temp_86 = temp_85;
    // 0x000388: 0x50900380E008700F Psetp
    temp_87 = temp_86;
    if (!temp_84)
    {
        temp_87 = true;
    }
    temp_88 = temp_87;
    // 0x000390: 0x3858103F00080700 Fadd
    temp_89 = as_type<float>(0);
    if (!temp_84)
    {
        temp_90 = temp_76 + 0.5;
        temp_89 = temp_90;
    }
    temp_91 = temp_89;
    // 0x000398: 0x3858103F00080901 Fadd
    temp_92 = as_type<float>(0);
    if (!temp_84)
    {
        temp_93 = temp_81 + 0.5;
        temp_92 = temp_93;
    }
    temp_94 = temp_92;
    // 0x0003A8: 0xE33000000009000F Kil
    if (!temp_88)
    {
        discard_fragment();
    }
    // 0x0003B0: 0xD832008020170000 Texs
    temp_95 = (is_null_texture(textures.tex_fp_t_tcb_8) ? decltype(textures.tex_fp_t_tcb_8.sample(textures.samp_fp_t_tcb_8, float2(temp_91, temp_94)).xyzw)(0) : textures.tex_fp_t_tcb_8.sample(textures.samp_fp_t_tcb_8, float2(temp_91, temp_94)).xyzw);
    temp_96 = temp_95.x;
    temp_97 = temp_95.y;
    temp_98 = temp_95.z;
    temp_99 = temp_95.w;
    // 0x0003B8: 0xE043FF8A0047FF09 Ipa
    temp_100 = in.inAttr2.x;
    // 0x0003C8: 0xE043FF890047FF0C Ipa
    temp_101 = in.inAttr1.x;
    // 0x0003D0: 0xE043FF8A4047FF0A Ipa
    temp_102 = in.inAttr2.y;
    // 0x0003D8: 0xE043FF894047FF0B Ipa
    temp_103 = in.inAttr1.y;
    // 0x0003E8: 0xE043FF8A8047FF05 Ipa
    temp_104 = in.inAttr2.z;
    // 0x0003F0: 0xE043FF898047FF06 Ipa
    temp_105 = in.inAttr1.z;
    // 0x0003F8: 0x5D2083902FF70307 Hsetp2
    temp_106 = temp_99 == 0.0f;
    // 0x000408: 0xE33000000000000F Kil
    if (temp_106)
    {
        discard_fragment();
    }
    // 0x000410: 0x4C68102400070007 Fmul
    temp_107 = temp_96 * constant_buffers.fp_c9->data[0].x;
    // 0x000418: 0x4C68102400170108 Fmul
    temp_108 = temp_97 * constant_buffers.fp_c9->data[0].y;
    // 0x000428: 0xE043FF89C047FF01 Ipa
    temp_109 = in.inAttr1.w;
    // 0x000430: 0x59A0048000C70700 Ffma
    temp_110 = fma(temp_107, temp_101, temp_100);
    // 0x000438: 0x59A0050000B70808 Ffma
    temp_111 = fma(temp_108, temp_103, temp_102);
    // 0x000448: 0x7A8508840017FF09 Hadd2
    temp_112 = -0.0f + constant_buffers.fp_c1->data[0].y;
    // 0x000450: 0x4C68102400270207 Fmul
    temp_113 = temp_98 * constant_buffers.fp_c9->data[0].z;
    // 0x000458: 0x7A84800400070000 Hadd2
    temp_114 = temp_110 + constant_buffers.fp_c1->data[0].x;
    // 0x000468: 0x7A86800400070800 Hadd2
    temp_115 = temp_111 + constant_buffers.fp_c1->data[0].x;
    // 0x000470: 0x59A0028000670705 Ffma
    temp_116 = fma(temp_113, temp_105, temp_104);
    // 0x000478: 0x70C004A80D570000 Hfma2
    temp_117 = fma(temp_114, constant_buffers.fp_c10->data[53].y, temp_112);
    temp_118 = fma(temp_115, constant_buffers.fp_c10->data[53].y, temp_112);
    // 0x000488: 0x7A84800400070505 Hadd2
    temp_119 = temp_116 + constant_buffers.fp_c1->data[0].x;
    // 0x000490: 0x7887000400270002 Hmul2
    temp_120 = temp_117 * constant_buffers.fp_c1->data[0].z;
    // 0x000498: 0x70C504A80D570505 Hfma2
    temp_121 = fma(temp_119, constant_buffers.fp_c10->data[53].y, temp_112);
    // 0x0004A8: 0x70E7810400370002 Hfma2
    temp_122 = fma(temp_118, constant_buffers.fp_c1->data[0].w, temp_120);
    // 0x0004B0: 0x4B6A03A40047FF07 Isetp
    temp_123 = 0 != as_type<int>(constant_buffers.fp_c9->data[1].x);
    // 0x0004B8: 0x70E7010400470502 Hfma2
    temp_124 = fma(temp_121, constant_buffers.fp_c1->data[1].x, temp_122);
    // 0x0004C8: 0x4C68102400370303 Fmul
    temp_125 = temp_99 * constant_buffers.fp_c9->data[0].w;
    // 0x0004D0: 0x71E781280D470202 Hfma2
    temp_126 = -constant_buffers.fp_c10->data[53].x;
    temp_127 = fma(temp_124, temp_126, temp_124);
    // 0x0004D8: 0x5C68100000170304 Fmul
    temp_128 = temp_125 * temp_109;
    // 0x0004E8: 0x38A00403C007FF07 Sel
    temp_129 = !temp_123;
    temp_130 = uint(temp_129 ? 0 : as_type<int>(0x3C00));
    // 0x0004F0: 0x70E301280D470006 Hfma2
    temp_131 = fma(temp_117, constant_buffers.fp_c10->data[53].x, temp_127);
    // 0x0004F8: 0x70E381280D470001 Hfma2
    temp_132 = fma(temp_118, constant_buffers.fp_c10->data[53].x, temp_127);
    // 0x000508: 0x70E301280D470502 Hfma2
    temp_133 = fma(temp_121, constant_buffers.fp_c10->data[53].x, temp_127);
    // 0x000510: 0x5D028208A0770403 Hfma2
    temp_134 = float2(as_type<half2>(temp_130)).x;
    temp_135 = -temp_134;
    temp_136 = fma(temp_128, temp_135, temp_128);
    // 0x000518: 0x5C68100000670400 Fmul
    temp_137 = temp_128 * temp_131;
    // 0x000528: 0x5C68100000170401 Fmul
    temp_138 = temp_128 * temp_132;
    // 0x000530: 0x5C68100000270402 Fmul
    temp_139 = temp_128 * temp_133;
    // 0x000538: 0xE30000000007000F Exit
    out.color0.x = temp_137;
    out.color0.y = temp_138;
    out.color0.z = temp_139;
    out.color0.w = temp_136;
    return out;
    return out;
}
