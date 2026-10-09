#pragma clang diagnostic ignored "-Wunused-variable"

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct VertexIn
{
    float4 inAttr0 [[attribute(0)]];
    float4 inAttr2 [[attribute(2)]];
};

struct VertexOut
{
    float point_size [[user(couch_transport_v1_ignored_point_size)]];
    float4 position [[position, invariant]];
    float4 outAttr0 [[user(loc0)]];
    float4 outAttr1 [[user(loc1)]];
    float4 outAttr2 [[user(loc2)]];
    float4 outAttr3 [[user(loc3)]];
    float4 outAttr4 [[user(loc4)]];
    float4 outAttr5 [[user(loc5)]];
    float4 outAttr6 [[user(loc6)]];
    float4 outAttr7 [[user(loc7)]];
    uint couch_transport_v1_clipW [[user(couch_clipW)]];
    uint4 couch_transport_v1_raw0 [[user(couch_raw0)]];
    uint4 couch_transport_v1_raw1 [[user(couch_raw1)]];
    uint4 couch_transport_v1_raw2 [[user(couch_raw2)]];
    uint4 couch_transport_v1_raw3 [[user(couch_raw3)]];
    uint4 couch_transport_v1_raw4 [[user(couch_raw4)]];
    uint4 couch_transport_v1_raw5 [[user(couch_raw5)]];
    uint4 couch_transport_v1_raw6 [[user(couch_raw6)]];
};
inline VertexOut couch_transport_v1_exit(VertexOut value) {
    value.couch_transport_v1_clipW=as_type<uint>(value.position.w);
    value.couch_transport_v1_raw0=as_type<uint4>(value.outAttr0);
    value.outAttr0=as_type<float4>(select(uint4(0u), value.couch_transport_v1_raw0, (value.couch_transport_v1_raw0&uint4(0x7f800000u))!=uint4(0x7f800000u)));
    value.couch_transport_v1_raw1=as_type<uint4>(value.outAttr1);
    value.outAttr1=as_type<float4>(select(uint4(0u), value.couch_transport_v1_raw1, (value.couch_transport_v1_raw1&uint4(0x7f800000u))!=uint4(0x7f800000u)));
    value.couch_transport_v1_raw2=as_type<uint4>(value.outAttr2);
    value.outAttr2=as_type<float4>(select(uint4(0u), value.couch_transport_v1_raw2, (value.couch_transport_v1_raw2&uint4(0x7f800000u))!=uint4(0x7f800000u)));
    value.couch_transport_v1_raw3=as_type<uint4>(value.outAttr3);
    value.outAttr3=as_type<float4>(select(uint4(0u), value.couch_transport_v1_raw3, (value.couch_transport_v1_raw3&uint4(0x7f800000u))!=uint4(0x7f800000u)));
    value.couch_transport_v1_raw4=as_type<uint4>(value.outAttr4);
    value.outAttr4=as_type<float4>(select(uint4(0u), value.couch_transport_v1_raw4, (value.couch_transport_v1_raw4&uint4(0x7f800000u))!=uint4(0x7f800000u)));
    value.couch_transport_v1_raw5=as_type<uint4>(value.outAttr5);
    value.outAttr5=as_type<float4>(select(uint4(0u), value.couch_transport_v1_raw5, (value.couch_transport_v1_raw5&uint4(0x7f800000u))!=uint4(0x7f800000u)));
    value.couch_transport_v1_raw6=as_type<uint4>(value.outAttr6);
    value.outAttr6=as_type<float4>(select(uint4(0u), value.couch_transport_v1_raw6, (value.couch_transport_v1_raw6&uint4(0x7f800000u))!=uint4(0x7f800000u)));
    return value;
}


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

struct struct_vp_c3
{
    float4 data[4096];
};

struct struct_vp_c4
{
    float4 data[4096];
};

struct ConstantBuffers
{
    constant struct_support_buffer* support_buffer;
    constant struct_vp_c3* vp_c3;
    constant struct_vp_c4* vp_c4;
};

struct StorageBuffers
{
};

vertex VertexOut vertexMain(VertexIn in [[stage_in]], 
                            uint vertex_id [[vertex_id]], 
                            uint instance_id [[instance_id]], 
                            uint base_instance [[base_instance]], 
                            uint base_vertex [[base_vertex]], 
                            constant ConstantBuffers &constant_buffers [[buffer(17)]], 
                            device StorageBuffers &storage_buffers [[buffer(18)]])
{
    VertexOut out = {};
    uint instance_index = instance_id + base_instance;
    uint thread_index_in_simdgroup = simd_prefix_exclusive_sum(1);
    float temp_0;
    int temp_1;
    float temp_2;
    int temp_3;
    uint temp_4;
    uint temp_5;
    int temp_6;
    float temp_7;
    int temp_8;
    uint temp_9;
    int temp_10;
    float temp_11;
    int temp_12;
    uint temp_13;
    uint temp_14;
    int temp_15;
    float temp_16;
    int temp_17;
    uint temp_18;
    int temp_19;
    float temp_20;
    int temp_21;
    uint temp_22;
    uint temp_23;
    int temp_24;
    float temp_25;
    int temp_26;
    uint temp_27;
    int temp_28;
    float temp_29;
    int temp_30;
    uint temp_31;
    uint temp_32;
    int temp_33;
    float temp_34;
    int temp_35;
    uint temp_36;
    int temp_37;
    float temp_38;
    int temp_39;
    uint temp_40;
    uint temp_41;
    int temp_42;
    float temp_43;
    int temp_44;
    uint temp_45;
    int temp_46;
    float temp_47;
    uint temp_48;
    uint temp_49;
    int temp_50;
    float temp_51;
    int temp_52;
    uint temp_53;
    int temp_54;
    float temp_55;
    int temp_56;
    uint temp_57;
    uint temp_58;
    int temp_59;
    float temp_60;
    int temp_61;
    uint temp_62;
    int temp_63;
    float temp_64;
    float temp_65;
    int temp_66;
    uint temp_67;
    uint temp_68;
    int temp_69;
    float temp_70;
    int temp_71;
    uint temp_72;
    int temp_73;
    float temp_74;
    float temp_75;
    float temp_76;
    float temp_77;
    float temp_78;
    float temp_79;
    float temp_80;
    float temp_81;
    float temp_82;
    float temp_83;
    float temp_84;
    float temp_85;
    float temp_86;
    float temp_87;
    float temp_88;
    float temp_89;
    float temp_90;
    float temp_91;
    float temp_92;
    float temp_93;
    float temp_94;
    float temp_95;
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
    float temp_106;
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
    float temp_123;
    float temp_124;
    float temp_125;
    float temp_126;
    float temp_127;
    float temp_128;
    float temp_129;
    float temp_130;
    float temp_131;
    float temp_132;
    float temp_133;
    float temp_134;
    float temp_135;
    float temp_136;
    float temp_137;
    float temp_138;
    float temp_139;
    float temp_140;
    float temp_141;
    float temp_142;
    float temp_143;
    float temp_144;
    float temp_145;
    float temp_146;
    float temp_147;
    float temp_148;
    float temp_149;
    float temp_150;
    float temp_151;
    float temp_152;
    float temp_153;
    float temp_154;
    float temp_155;
    float temp_156;
    float temp_157;
    float temp_158;
    float temp_159;
    float temp_160;
    float temp_161;
    float temp_162;
    float temp_163;
    float temp_164;
    float temp_165;
    float temp_166;
    float temp_167;
    float temp_168;
    float temp_169;
    float temp_170;
    float temp_171;
    float temp_172;
    float temp_173;
    float temp_174;
    float temp_175;
    float temp_176;
    float temp_177;
    float temp_178;
    float temp_179;
    float temp_180;
    float temp_181;
    float temp_182;
    float temp_183;
    float temp_184;
    float temp_185;
    float temp_186;
    float temp_187;
    float temp_188;
    float temp_189;
    float temp_190;
    float temp_191;
    float temp_192;
    float temp_193;
    float temp_194;
    float temp_195;
    float temp_196;
    float temp_197;
    float temp_198;
    float temp_199;
    float temp_200;
    float temp_201;
    float temp_202;
    float temp_203;
    float temp_204;
    float temp_205;
    float temp_206;
    float temp_207;
    float temp_208;
    float temp_209;
    float temp_210;
    float temp_211;
    float temp_212;
    float temp_213;
    float temp_214;
    float temp_215;
    float temp_216;
    float temp_217;
    float temp_218;
    float temp_219;
    float temp_220;
    float temp_221;
    float temp_222;
    float temp_223;
    float temp_224;
    float temp_225;
    float temp_226;
    float temp_227;
    float temp_228;
    float temp_229;
    float temp_230;
    float temp_231;
    float temp_232;
    float temp_233;
    float temp_234;
    float temp_235;
    float temp_236;
    float temp_237;
    float temp_238;
    float temp_239;
    float temp_240;
    float temp_241;
    float temp_242;
    float temp_243;
    float temp_244;
    float temp_245;
    float temp_246;
    float temp_247;
    float temp_248;
    float temp_249;
    float temp_250;
    float temp_251;
    float temp_252;
    float temp_253;
    float temp_254;
    float temp_255;
    float temp_256;
    float temp_257;
    float temp_258;
    float temp_259;
    float temp_260;
    float temp_261;
    float temp_262;
    float temp_263;
    float temp_264;
    float temp_265;
    float temp_266;
    float temp_267;
    float temp_268;
    float temp_269;
    float temp_270;
    float temp_271;
    float temp_272;
    float temp_273;
    float temp_274;
    float temp_275;
    float temp_276;
    float temp_277;
    float temp_278;
    float temp_279;
    float temp_280;
    float temp_281;
    float temp_282;
    float temp_283;
    float temp_284;
    float temp_285;
    float temp_286;
    float temp_287;
    float temp_288;
    float temp_289;
    float temp_290;
    float temp_291;
    float temp_292;
    float temp_293;
    float temp_294;
    float temp_295;
    float temp_296;
    float temp_297;
    float temp_298;
    float temp_299;
    float temp_300;
    float temp_301;
    float temp_302;
    float temp_303;
    float temp_304;
    float temp_305;
    float temp_306;
    float temp_307;
    float temp_308;
    float temp_309;
    float temp_310;
    float temp_311;
    float temp_312;
    float temp_313;
    float temp_314;
    float temp_315;
    float temp_316;
    float temp_317;
    float temp_318;
    float temp_319;
    float temp_320;
    float temp_321;
    float temp_322;
    float temp_323;
    float temp_324;
    float temp_325;
    float temp_326;
    float temp_327;
    float temp_328;
    float temp_329;
    float temp_330;
    float temp_331;
    float temp_332;
    float temp_333;
    float temp_334;
    float temp_335;
    float temp_336;
    float temp_337;
    float temp_338;
    float temp_339;
    float temp_340;
    float temp_341;
    float temp_342;
    float temp_343;
    float temp_344;
    float temp_345;
    float temp_346;
    float temp_347;
    float temp_348;
    float temp_349;
    float temp_350;
    float temp_351;
    float temp_352;
    float temp_353;
    float temp_354;
    float temp_355;
    float temp_356;
    float temp_357;
    float temp_358;
    float temp_359;
    float temp_360;
    float temp_361;
    float temp_362;
    float temp_363;
    float temp_364;
    float temp_365;
    float temp_366;
    float temp_367;
    float temp_368;
    float temp_369;
    float temp_370;
    float temp_371;
    float temp_372;
    float temp_373;
    float temp_374;
    out.point_size = 1.0f;
    out.position.x = 0.0f;
    out.position.y = 0.0f;
    out.position.z = 0.0f;
    out.position.w = 1.0f;
    out.outAttr0.x = 0.0f;
    out.outAttr0.y = 0.0f;
    out.outAttr0.w = 1.0f;
    out.outAttr1.x = 0.0f;
    out.outAttr1.y = 0.0f;
    out.outAttr1.z = 0.0f;
    out.outAttr1.w = 1.0f;
    out.outAttr2.x = 0.0f;
    out.outAttr2.y = 0.0f;
    out.outAttr2.z = 0.0f;
    out.outAttr3.x = 0.0f;
    out.outAttr3.y = 0.0f;
    out.outAttr3.z = 0.0f;
    out.outAttr3.w = 1.0f;
    out.outAttr4.x = 0.0f;
    out.outAttr4.y = 0.0f;
    out.outAttr4.z = 0.0f;
    out.outAttr4.w = 1.0f;
    out.outAttr5.x = 0.0f;
    out.outAttr5.y = 0.0f;
    out.outAttr5.z = 0.0f;
    out.outAttr5.w = 1.0f;
    out.outAttr6.x = 0.0f;
    out.outAttr6.y = 0.0f;
    out.outAttr6.z = 0.0f;
    out.outAttr6.w = 1.0f;
    // 0x000008: 0xEFD87F802F87FF28 Ald
    temp_0 = as_type<float>(instance_id);
    // 0x000010: 0x3848000000672810 Shl
    temp_1 = as_type<int>(temp_0) << 6;
    // 0x000018: 0xEFD87F800807FF14 Ald
    temp_2 = in.inAttr0.x;
    // 0x000028: 0xEF95003001871000 Ldc
    temp_3 = temp_1 + 24;
    temp_4 = uint(temp_3) >> 2;
    temp_5 = temp_4 >> 2;
    temp_6 = int(temp_4) & 3;
    temp_7 = constant_buffers.vp_c3->data[int(temp_5)][temp_6];
    temp_8 = int(temp_4) + 1;
    temp_9 = uint(temp_8) >> 2;
    temp_10 = temp_8 & 3;
    temp_11 = constant_buffers.vp_c3->data[int(temp_9)][temp_10];
    // 0x000030: 0xEF95003002871008 Ldc
    temp_12 = temp_1 + 40;
    temp_13 = uint(temp_12) >> 2;
    temp_14 = temp_13 >> 2;
    temp_15 = int(temp_13) & 3;
    temp_16 = constant_buffers.vp_c3->data[int(temp_14)][temp_15];
    temp_17 = int(temp_13) + 1;
    temp_18 = uint(temp_17) >> 2;
    temp_19 = temp_17 & 3;
    temp_20 = constant_buffers.vp_c3->data[int(temp_18)][temp_19];
    // 0x000038: 0xEF9500300307100E Ldc
    temp_21 = temp_1 + 48;
    temp_22 = uint(temp_21) >> 2;
    temp_23 = temp_22 >> 2;
    temp_24 = int(temp_22) & 3;
    temp_25 = constant_buffers.vp_c3->data[int(temp_23)][temp_24];
    temp_26 = int(temp_22) + 1;
    temp_27 = uint(temp_26) >> 2;
    temp_28 = temp_26 & 3;
    temp_29 = constant_buffers.vp_c3->data[int(temp_27)][temp_28];
    // 0x000048: 0xEF95003000871004 Ldc
    temp_30 = temp_1 + 8;
    temp_31 = uint(temp_30) >> 2;
    temp_32 = temp_31 >> 2;
    temp_33 = int(temp_31) & 3;
    temp_34 = constant_buffers.vp_c3->data[int(temp_32)][temp_33];
    temp_35 = int(temp_31) + 1;
    temp_36 = uint(temp_35) >> 2;
    temp_37 = temp_35 & 3;
    temp_38 = constant_buffers.vp_c3->data[int(temp_36)][temp_37];
    // 0x000050: 0xEF9500300207100A Ldc
    temp_39 = temp_1 + 32;
    temp_40 = uint(temp_39) >> 2;
    temp_41 = temp_40 >> 2;
    temp_42 = int(temp_40) & 3;
    temp_43 = constant_buffers.vp_c3->data[int(temp_41)][temp_42];
    temp_44 = int(temp_40) + 1;
    temp_45 = uint(temp_44) >> 2;
    temp_46 = temp_44 & 3;
    temp_47 = constant_buffers.vp_c3->data[int(temp_45)][temp_46];
    // 0x000058: 0xEF95003000071006 Ldc
    temp_48 = uint(temp_1) >> 2;
    temp_49 = temp_48 >> 2;
    temp_50 = int(temp_48) & 3;
    temp_51 = constant_buffers.vp_c3->data[int(temp_49)][temp_50];
    temp_52 = int(temp_48) + 1;
    temp_53 = uint(temp_52) >> 2;
    temp_54 = temp_52 & 3;
    temp_55 = constant_buffers.vp_c3->data[int(temp_53)][temp_54];
    // 0x000068: 0xEF95003001071002 Ldc
    temp_56 = temp_1 + 16;
    temp_57 = uint(temp_56) >> 2;
    temp_58 = temp_57 >> 2;
    temp_59 = int(temp_57) & 3;
    temp_60 = constant_buffers.vp_c3->data[int(temp_58)][temp_59];
    temp_61 = int(temp_57) + 1;
    temp_62 = uint(temp_61) >> 2;
    temp_63 = temp_61 & 3;
    temp_64 = constant_buffers.vp_c3->data[int(temp_62)][temp_63];
    // 0x000070: 0xEFD87F800847FF15 Ald
    temp_65 = in.inAttr0.y;
    // 0x000078: 0xEF9500300387100C Ldc
    temp_66 = temp_1 + 56;
    temp_67 = uint(temp_66) >> 2;
    temp_68 = temp_67 >> 2;
    temp_69 = int(temp_67) & 3;
    temp_70 = constant_buffers.vp_c3->data[int(temp_68)][temp_69];
    temp_71 = int(temp_67) + 1;
    temp_72 = uint(temp_71) >> 2;
    temp_73 = temp_71 & 3;
    temp_74 = constant_buffers.vp_c3->data[int(temp_72)][temp_73];
    // 0x000088: 0xEFD87F800887FF17 Ald
    temp_75 = in.inAttr0.z;
    // 0x000090: 0x4C68101000171413 Fmul
    temp_76 = temp_2 * constant_buffers.vp_c4->data[0].y;
    // 0x000098: 0x4C68101000371411 Fmul
    temp_77 = temp_2 * constant_buffers.vp_c4->data[0].w;
    // 0x0000A8: 0x4C68101000271412 Fmul
    temp_78 = temp_2 * constant_buffers.vp_c4->data[0].z;
    // 0x0000B0: 0x4C68101000071414 Fmul
    temp_79 = temp_2 * constant_buffers.vp_c4->data[0].x;
    // 0x0000B8: 0x49A0099000571513 Ffma
    temp_80 = fma(temp_65, constant_buffers.vp_c4->data[1].y, temp_76);
    // 0x0000C8: 0x49A0089000771511 Ffma
    temp_81 = fma(temp_65, constant_buffers.vp_c4->data[1].w, temp_77);
    // 0x0000D0: 0x5C68100000870118 Fmul
    temp_82 = temp_11 * temp_16;
    // 0x0000D8: 0x5C68100000970019 Fmul
    temp_83 = temp_7 * temp_20;
    // 0x0000E8: 0x49A0091000671512 Ffma
    temp_84 = fma(temp_65, constant_buffers.vp_c4->data[1].z, temp_78);
    // 0x0000F0: 0x49A00A1000471510 Ffma
    temp_85 = fma(temp_65, constant_buffers.vp_c4->data[1].x, temp_79);
    // 0x0000F8: 0x49A0099000971715 Ffma
    temp_86 = fma(temp_75, constant_buffers.vp_c4->data[2].y, temp_80);
    // 0x000108: 0x49A0089000B71716 Ffma
    temp_87 = fma(temp_75, constant_buffers.vp_c4->data[2].w, temp_81);
    // 0x000110: 0x5C68100001870F14 Fmul
    temp_88 = temp_29 * temp_82;
    // 0x000118: 0x5C68100001970E13 Fmul
    temp_89 = temp_25 * temp_83;
    // 0x000128: 0x5C68100000970411 Fmul
    temp_90 = temp_34 * temp_20;
    // 0x000130: 0x5C68100000B7011B Fmul
    temp_91 = temp_11 * temp_47;
    // 0x000138: 0x49A0091000A71712 Ffma
    temp_92 = fma(temp_75, constant_buffers.vp_c4->data[2].z, temp_84);
    // 0x000148: 0x49A0081000871710 Ffma
    temp_93 = fma(temp_75, constant_buffers.vp_c4->data[2].x, temp_85);
    // 0x000150: 0x59A20A0001970F22 Ffma
    temp_94 = -temp_88;
    temp_95 = fma(temp_29, temp_83, temp_94);
    // 0x000158: 0x59A2098001870E2B Ffma
    temp_96 = -temp_89;
    temp_97 = fma(temp_25, temp_82, temp_96);
    // 0x000168: 0x5C68100000870514 Fmul
    temp_98 = temp_38 * temp_16;
    // 0x000170: 0x5C68100000970717 Fmul
    temp_99 = temp_55 * temp_20;
    // 0x000178: 0x5C68100001170F19 Fmul
    temp_100 = temp_29 * temp_90;
    // 0x000188: 0x5C68100000970321 Fmul
    temp_101 = temp_64 * temp_20;
    // 0x000190: 0x5C68100001B70E18 Fmul
    temp_102 = temp_25 * temp_91;
    // 0x000198: 0x5C68100000B70513 Fmul
    temp_103 = temp_38 * temp_47;
    // 0x0001A8: 0x5C68100001470E1C Fmul
    temp_104 = temp_25 * temp_98;
    // 0x0001B0: 0x5C68100001770E1D Fmul
    temp_105 = temp_25 * temp_99;
    // 0x0001B8: 0x59A20C8001470F19 Ffma
    temp_106 = -temp_100;
    temp_107 = fma(temp_29, temp_98, temp_106);
    // 0x0001C8: 0x5C68100000A7011A Fmul
    temp_108 = temp_11 * temp_43;
    // 0x0001D0: 0x59A20C0002170E18 Ffma
    temp_109 = -temp_102;
    temp_110 = fma(temp_25, temp_101, temp_109);
    // 0x0001D8: 0x5C68100000A70526 Fmul
    temp_111 = temp_38 * temp_43;
    // 0x0001E8: 0x59A20E0001170E1F Ffma
    temp_112 = -temp_104;
    temp_113 = fma(temp_25, temp_90, temp_112);
    // 0x0001F0: 0x59A20E8001370E1D Ffma
    temp_114 = -temp_105;
    temp_115 = fma(temp_25, temp_103, temp_114);
    // 0x0001F8: 0x59A10C8001370C1E Ffma
    temp_116 = -temp_103;
    temp_117 = fma(temp_70, temp_116, temp_107);
    // 0x000208: 0x4C68100C03770711 Fmul
    temp_118 = temp_55 * constant_buffers.vp_c3->data[13].w;
    // 0x000210: 0x59A00C0001A70F24 Ffma
    temp_119 = fma(temp_29, temp_108, temp_110);
    // 0x000218: 0x4C68100C03470714 Fmul
    temp_120 = temp_55 * constant_buffers.vp_c3->data[13].x;
    // 0x000228: 0x4C68100C03570718 Fmul
    temp_121 = temp_55 * constant_buffers.vp_c3->data[13].y;
    // 0x000230: 0x4C68100C03670713 Fmul
    temp_122 = temp_55 * constant_buffers.vp_c3->data[13].z;
    // 0x000238: 0x59A0110001B70C22 Ffma
    temp_123 = fma(temp_70, temp_91, temp_95);
    // 0x000248: 0x59A1158001A70C2B Ffma
    temp_124 = -temp_108;
    temp_125 = fma(temp_70, temp_124, temp_97);
    // 0x000250: 0x59A00F8002670C1B Ffma
    temp_126 = fma(temp_70, temp_111, temp_113);
    // 0x000258: 0x49A0088C03370611 Ffma
    temp_127 = fma(temp_51, constant_buffers.vp_c3->data[12].w, temp_118);
    // 0x000268: 0x49A00A0C0307061A Ffma
    temp_128 = fma(temp_51, constant_buffers.vp_c3->data[12].x, temp_120);
    // 0x000270: 0x49A00C0C0317061F Ffma
    temp_129 = fma(temp_51, constant_buffers.vp_c3->data[12].y, temp_121);
    // 0x000278: 0x49A0098C03270613 Ffma
    temp_130 = fma(temp_51, constant_buffers.vp_c3->data[12].z, temp_122);
    // 0x000288: 0x5C68100000470118 Fmul
    temp_131 = temp_11 * temp_34;
    // 0x000290: 0x5C68100000570019 Fmul
    temp_132 = temp_7 * temp_38;
    // 0x000298: 0x49A0088C03B70414 Ffma
    temp_133 = fma(temp_34, constant_buffers.vp_c3->data[14].w, temp_127);
    // 0x0002A8: 0x49A00D0C0387041A Ffma
    temp_134 = fma(temp_34, constant_buffers.vp_c3->data[14].x, temp_128);
    // 0x0002B0: 0x49A00F8C0397041C Ffma
    temp_135 = fma(temp_34, constant_buffers.vp_c3->data[14].y, temp_129);
    // 0x0002B8: 0x49A0098C03A70413 Ffma
    temp_136 = fma(temp_34, constant_buffers.vp_c3->data[14].z, temp_130);
    // 0x0002C8: 0x4C58101000C71023 Fadd
    temp_137 = temp_93 + constant_buffers.vp_c4->data[3].x;
    // 0x0002D0: 0x5C68100001870B20 Fmul
    temp_138 = temp_47 * temp_131;
    // 0x0002D8: 0x49A00A0C03F70514 Ffma
    temp_139 = fma(temp_38, constant_buffers.vp_c3->data[15].w, temp_133);
    // 0x0002E8: 0x49A00D0C03C7051A Ffma
    temp_140 = fma(temp_38, constant_buffers.vp_c3->data[15].x, temp_134);
    // 0x0002F0: 0x49A00E0C03D70510 Ffma
    temp_141 = fma(temp_38, constant_buffers.vp_c3->data[15].y, temp_135);
    // 0x0002F8: 0x49A0098C03E70511 Ffma
    temp_142 = fma(temp_38, constant_buffers.vp_c3->data[15].z, temp_136);
    // 0x000308: 0x5C68100001970F25 Fmul
    temp_143 = temp_29 * temp_132;
    // 0x000310: 0x59A10E8002670F1D Ffma
    temp_144 = -temp_111;
    temp_145 = fma(temp_29, temp_144, temp_115);
    // 0x000318: 0x5C6810000077011F Fmul
    temp_146 = temp_11 * temp_55;
    // 0x000328: 0x5C6810000237141C Fmul
    temp_147 = temp_139 * temp_137;
    // 0x000330: 0x5C68100002371A13 Fmul
    temp_148 = temp_140 * temp_137;
    // 0x000338: 0x5C68100002371010 Fmul
    temp_149 = temp_141 * temp_137;
    // 0x000348: 0x5C68100002371111 Fmul
    temp_150 = temp_142 * temp_137;
    // 0x000350: 0x5C68100000570329 Fmul
    temp_151 = temp_64 * temp_38;
    // 0x000358: 0x59A2100001970B26 Ffma
    temp_152 = -temp_138;
    temp_153 = fma(temp_47, temp_132, temp_152);
    // 0x000368: 0x59A2128001870F23 Ffma
    temp_154 = -temp_143;
    temp_155 = fma(temp_29, temp_131, temp_154);
    // 0x000370: 0x4C68100C03770314 Fmul
    temp_156 = temp_64 * constant_buffers.vp_c3->data[13].w;
    // 0x000378: 0x5C68100001F70A27 Fmul
    temp_157 = temp_43 * temp_146;
    // 0x000388: 0x59A1110002170C22 Ffma
    temp_158 = -temp_101;
    temp_159 = fma(temp_70, temp_158, temp_123);
    // 0x000390: 0x59A1130002970821 Ffma
    temp_160 = -temp_151;
    temp_161 = fma(temp_16, temp_160, temp_153);
    // 0x000398: 0x59A0118002970C26 Ffma
    temp_162 = fma(temp_70, temp_151, temp_155);
    // 0x0003A8: 0x4C68100C03470325 Fmul
    temp_163 = temp_64 * constant_buffers.vp_c3->data[13].x;
    // 0x0003B0: 0x49A00A0C03370223 Ffma
    temp_164 = fma(temp_60, constant_buffers.vp_c3->data[12].w, temp_156);
    // 0x0003B8: 0x59A2138002970A1A Ffma
    temp_165 = -temp_157;
    temp_166 = fma(temp_43, temp_151, temp_165);
    // 0x0003C8: 0x5C68100002970E29 Fmul
    temp_167 = temp_25 * temp_151;
    // 0x0003D0: 0x4C68100C03570327 Fmul
    temp_168 = temp_64 * constant_buffers.vp_c3->data[13].y;
    // 0x0003D8: 0x4C68100C03670320 Fmul
    temp_169 = temp_64 * constant_buffers.vp_c3->data[13].z;
    // 0x0003E8: 0x49A0128C03070225 Ffma
    temp_170 = fma(temp_60, constant_buffers.vp_c3->data[12].x, temp_163);
    // 0x0003F0: 0x49A0118C03B70023 Ffma
    temp_171 = fma(temp_7, constant_buffers.vp_c3->data[14].w, temp_164);
    // 0x0003F8: 0x59A00F0001770C17 Ffma
    temp_172 = fma(temp_70, temp_99, temp_117);
    // 0x000408: 0x59A2148001F70E14 Ffma
    temp_173 = -temp_167;
    temp_174 = fma(temp_25, temp_146, temp_173);
    // 0x000410: 0x59A1130001F70C1E Ffma
    temp_175 = -temp_146;
    temp_176 = fma(temp_70, temp_175, temp_162);
    // 0x000418: 0x59A0108001F7081F Ffma
    temp_177 = fma(temp_16, temp_146, temp_161);
    // 0x000428: 0x49A0138C03170229 Ffma
    temp_178 = fma(temp_60, constant_buffers.vp_c3->data[12].y, temp_168);
    // 0x000430: 0x4C58101000D71526 Fadd
    temp_179 = temp_86 + constant_buffers.vp_c4->data[3].y;
    // 0x000438: 0x49A0100C03270221 Ffma
    temp_180 = fma(temp_60, constant_buffers.vp_c3->data[12].z, temp_169);
    // 0x000448: 0x49A0128C03870027 Ffma
    temp_181 = fma(temp_7, constant_buffers.vp_c3->data[14].x, temp_170);
    // 0x000450: 0x49A0118C03F70115 Ffma
    temp_182 = fma(temp_11, constant_buffers.vp_c3->data[15].w, temp_171);
    // 0x000458: 0x49A0148C03970023 Ffma
    temp_183 = fma(temp_7, constant_buffers.vp_c3->data[14].y, temp_178);
    // 0x000468: 0x49A0108C03A70020 Ffma
    temp_184 = fma(temp_7, constant_buffers.vp_c3->data[14].z, temp_180);
    // 0x000470: 0x49A0138C03C70125 Ffma
    temp_185 = fma(temp_11, constant_buffers.vp_c3->data[15].x, temp_181);
    // 0x000478: 0x59A00E0002671515 Ffma
    temp_186 = fma(temp_182, temp_179, temp_147);
    // 0x000488: 0x5C6810000087031C Fmul
    temp_187 = temp_64 * temp_16;
    // 0x000490: 0x49A0118C03D70121 Ffma
    temp_188 = fma(temp_11, constant_buffers.vp_c3->data[15].y, temp_183);
    // 0x000498: 0x5C68100000B70023 Fmul
    temp_189 = temp_7 * temp_47;
    // 0x0004A8: 0x49A0100C03E70120 Ffma
    temp_190 = fma(temp_11, constant_buffers.vp_c3->data[15].z, temp_184);
    // 0x0004B0: 0x59A0098002572613 Ffma
    temp_191 = fma(temp_179, temp_185, temp_148);
    // 0x0004B8: 0x5C68100000B7042C Fmul
    temp_192 = temp_34 * temp_47;
    // 0x0004C8: 0x5C68100001C70E25 Fmul
    temp_193 = temp_25 * temp_187;
    // 0x0004D0: 0x5C6810000097022A Fmul
    temp_194 = temp_60 * temp_20;
    // 0x0004D8: 0x5C6810000097062E Fmul
    temp_195 = temp_51 * temp_20;
    // 0x0004E8: 0x59A0080002172610 Ffma
    temp_196 = fma(temp_179, temp_188, temp_149);
    // 0x0004F0: 0x59A0088002672011 Ffma
    temp_197 = fma(temp_190, temp_179, temp_150);
    // 0x0004F8: 0x5C68100000A70026 Fmul
    temp_198 = temp_7 * temp_43;
    // 0x000508: 0x59A2128002370E25 Ffma
    temp_199 = -temp_193;
    temp_200 = fma(temp_25, temp_189, temp_199);
    // 0x000510: 0x5C68100000870720 Fmul
    temp_201 = temp_55 * temp_16;
    // 0x000518: 0x5C68100002C70E21 Fmul
    temp_202 = temp_25 * temp_192;
    // 0x000528: 0x59A1120002A70F24 Ffma
    temp_203 = -temp_194;
    temp_204 = fma(temp_29, temp_203, temp_119);
    // 0x000530: 0x59A0158002A70C2B Ffma
    temp_205 = fma(temp_70, temp_194, temp_125);
    // 0x000538: 0x59A00E8002E70F2A Ffma
    temp_206 = fma(temp_29, temp_195, temp_145);
    // 0x000548: 0x59A10D8002E70C1B Ffma
    temp_207 = -temp_195;
    temp_208 = fma(temp_70, temp_207, temp_126);
    // 0x000550: 0x59A1110002370D2E Ffma
    temp_209 = -temp_189;
    temp_210 = fma(temp_74, temp_209, temp_159);
    // 0x000558: 0x5C68100000870222 Fmul
    temp_211 = temp_60 * temp_16;
    // 0x000568: 0x59A1128002670F25 Ffma
    temp_212 = -temp_198;
    temp_213 = fma(temp_29, temp_212, temp_200);
    // 0x000570: 0x59A2108002070E21 Ffma
    temp_214 = -temp_202;
    temp_215 = fma(temp_25, temp_201, temp_214);
    // 0x000578: 0x5C68100000A7041D Fmul
    temp_216 = temp_34 * temp_43;
    // 0x000588: 0x59A00B8002C70D2F Ffma
    temp_217 = fma(temp_74, temp_192, temp_172);
    // 0x000590: 0x59A0158002670D2B Ffma
    temp_218 = fma(temp_74, temp_198, temp_205);
    // 0x000598: 0x5C68100000A70317 Fmul
    temp_219 = temp_64 * temp_43;
    // 0x0005A8: 0x59A0128002270F25 Ffma
    temp_220 = fma(temp_29, temp_211, temp_213);
    // 0x0005B0: 0x5C68100000870626 Fmul
    temp_221 = temp_51 * temp_16;
    // 0x0005B8: 0x59A0108001D70F21 Ffma
    temp_222 = fma(temp_29, temp_216, temp_215);
    // 0x0005C8: 0x59A1120001770D24 Ffma
    temp_223 = -temp_219;
    temp_224 = fma(temp_74, temp_223, temp_204);
    // 0x0005D0: 0x59A0128001770C23 Ffma
    temp_225 = fma(temp_70, temp_219, temp_220);
    // 0x0005D8: 0x59A1108002670F17 Ffma
    temp_226 = -temp_221;
    temp_227 = fma(temp_29, temp_226, temp_222);
    // 0x0005E8: 0x5C68100000A70721 Fmul
    temp_228 = temp_55 * temp_43;
    // 0x0005F0: 0x5C6810000077002C Fmul
    temp_229 = temp_7 * temp_55;
    // 0x0005F8: 0x59A10D8001D70D27 Ffma
    temp_230 = -temp_216;
    temp_231 = fma(temp_74, temp_230, temp_208);
    // 0x000608: 0x59A10B8002170C17 Ffma
    temp_232 = -temp_228;
    temp_233 = fma(temp_70, temp_232, temp_227);
    // 0x000610: 0x59A0150002170D1B Ffma
    temp_234 = fma(temp_74, temp_228, temp_206);
    // 0x000618: 0x5C68100000470321 Fmul
    temp_235 = temp_64 * temp_34;
    // 0x000628: 0x5C68100002C70E25 Fmul
    temp_236 = temp_25 * temp_229;
    // 0x000630: 0x59A0170001C70D1C Ffma
    temp_237 = fma(temp_74, temp_187, temp_210);
    // 0x000638: 0x59A10F0002170D2E Ffma
    temp_238 = -temp_235;
    temp_239 = fma(temp_74, temp_238, temp_176);
    // 0x000648: 0x59A2128002170E1E Ffma
    temp_240 = -temp_236;
    temp_241 = fma(temp_25, temp_235, temp_240);
    // 0x000650: 0x5C68100000B70225 Fmul
    temp_242 = temp_60 * temp_47;
    // 0x000658: 0x59A1178002070D1D Ffma
    temp_243 = -temp_201;
    temp_244 = fma(temp_74, temp_243, temp_217);
    // 0x000668: 0x5C68100000470204 Fmul
    temp_245 = temp_60 * temp_34;
    // 0x000670: 0x5C68100000570205 Fmul
    temp_246 = temp_60 * temp_38;
    // 0x000678: 0x59A1118002570C23 Ffma
    temp_247 = -temp_242;
    temp_248 = fma(temp_70, temp_247, temp_225);
    // 0x000688: 0x59A0120002570D24 Ffma
    temp_249 = fma(temp_74, temp_242, temp_224);
    // 0x000690: 0x59A0138002670D25 Ffma
    temp_250 = fma(temp_74, temp_221, temp_231);
    // 0x000698: 0x5C68100001D70227 Fmul
    temp_251 = temp_60 * temp_244;
    // 0x0006A8: 0x59A00F800217092A Ffma
    temp_252 = fma(temp_20, temp_235, temp_177);
    // 0x0006B0: 0x5C68100002170A29 Fmul
    temp_253 = temp_43 * temp_235;
    // 0x0006B8: 0x5C68100000770207 Fmul
    temp_254 = temp_60 * temp_55;
    // 0x0006C8: 0x5C68100000670101 Fmul
    temp_255 = temp_11 * temp_51;
    // 0x0006D0: 0x5C68100000670000 Fmul
    temp_256 = temp_7 * temp_51;
    // 0x0006D8: 0x59A10F0000470F02 Ffma
    temp_257 = -temp_245;
    temp_258 = fma(temp_29, temp_257, temp_241);
    // 0x0006E8: 0x59A00A0000570F14 Ffma
    temp_259 = fma(temp_29, temp_246, temp_174);
    // 0x0006F0: 0x59A0138001C7062D Ffma
    temp_260 = fma(temp_51, temp_237, temp_251);
    // 0x0006F8: 0x59A1158002270D22 Ffma
    temp_261 = -temp_211;
    temp_262 = fma(temp_74, temp_261, temp_218);
    // 0x000708: 0x4C68100C03570F27 Fmul
    temp_263 = temp_29 * constant_buffers.vp_c3->data[13].y;
    // 0x000710: 0x59A0170002C70D1F Ffma
    temp_264 = fma(temp_74, temp_229, temp_239);
    // 0x000718: 0x59A1150002C70921 Ffma
    temp_265 = -temp_229;
    temp_266 = fma(temp_20, temp_265, temp_252);
    // 0x000728: 0x4C68100C03670F2B Fmul
    temp_267 = temp_29 * constant_buffers.vp_c3->data[13].z;
    // 0x000730: 0x5C68100001870E2A Fmul
    temp_268 = temp_25 * temp_131;
    // 0x000738: 0x59A2148002C70A20 Ffma
    temp_269 = -temp_253;
    temp_270 = fma(temp_43, temp_229, temp_269);
    // 0x000748: 0x4C68100C03770F29 Fmul
    temp_271 = temp_29 * constant_buffers.vp_c3->data[13].w;
    // 0x000750: 0x4C68100C03470F2C Fmul
    temp_272 = temp_29 * constant_buffers.vp_c3->data[13].x;
    // 0x000758: 0x59A0010000070F02 Ffma
    temp_273 = fma(temp_29, temp_256, temp_258);
    // 0x000768: 0x59A10A0000170F14 Ffma
    temp_274 = -temp_255;
    temp_275 = fma(temp_29, temp_274, temp_259);
    // 0x000770: 0x49A0138C03170E0F Ffma
    temp_276 = fma(temp_25, constant_buffers.vp_c3->data[12].y, temp_263);
    // 0x000778: 0x59A0168001F70A2F Ffma
    temp_277 = fma(temp_43, temp_264, temp_260);
    // 0x000788: 0x49A0158C03270E27 Ffma
    temp_278 = fma(temp_25, constant_buffers.vp_c3->data[12].z, temp_267);
    // 0x000790: 0x59A2150001970E2B Ffma
    temp_279 = -temp_268;
    temp_280 = fma(temp_25, temp_132, temp_279);
    // 0x000798: 0x5C68100001970A2D Fmul
    temp_281 = temp_43 * temp_132;
    // 0x0007A8: 0x4C68100C03770B19 Fmul
    temp_282 = temp_47 * constant_buffers.vp_c3->data[13].w;
    // 0x0007B0: 0x4C68100C03570B2E Fmul
    temp_283 = temp_47 * constant_buffers.vp_c3->data[13].y;
    // 0x0007B8: 0x49A00C8C03370A2A Ffma
    temp_284 = fma(temp_43, constant_buffers.vp_c3->data[12].w, temp_282);
    // 0x0007C8: 0x49A0170C03170A19 Ffma
    temp_285 = fma(temp_43, constant_buffers.vp_c3->data[12].y, temp_283);
    // 0x0007D0: 0x59A2168001870A2E Ffma
    temp_286 = -temp_281;
    temp_287 = fma(temp_43, temp_131, temp_286);
    // 0x0007D8: 0xEFD87F800A07FF18 Ald
    temp_288 = in.inAttr2.x;
    // 0x0007E8: 0x49A0160C03070E1E Ffma
    temp_289 = fma(temp_25, constant_buffers.vp_c3->data[12].x, temp_272);
    // 0x0007F0: 0x59A0178002170E2C Ffma
    temp_290 = fma(temp_25, temp_266, temp_277);
    // 0x0007F8: 0x4C68100C03470B2F Fmul
    temp_291 = temp_47 * constant_buffers.vp_c3->data[13].x;
    // 0x000808: 0x49A0148C03370E26 Ffma
    temp_292 = fma(temp_25, constant_buffers.vp_c3->data[12].w, temp_271);
    // 0x000810: 0x4C68100C03670B29 Fmul
    temp_293 = temp_47 * constant_buffers.vp_c3->data[13].z;
    // 0x000818: 0x59A0100000470B2D Ffma
    temp_294 = fma(temp_47, temp_245, temp_270);
    // 0x000828: 0x49A0178C03070A0E Ffma
    temp_295 = fma(temp_43, constant_buffers.vp_c3->data[12].x, temp_291);
    // 0x000830: 0xEFD87F800A47FF2F Ald
    temp_296 = in.inAttr2.y;
    // 0x000838: 0x49A0148C03270A29 Ffma
    temp_297 = fma(temp_43, constant_buffers.vp_c3->data[12].z, temp_293);
    // 0x000848: 0x5080000000472C0A Mufu
    temp_298 = 1.0f / temp_290;
    // 0x000850: 0x59A1158000570C2B Ffma
    temp_299 = -temp_246;
    temp_300 = fma(temp_70, temp_299, temp_280);
    // 0x000858: 0xEFF07F800AC7FFFF Ast
    out.outAttr2.w = 0.0f;
    // 0x000868: 0x59A017000057082E Ffma
    temp_301 = fma(temp_16, temp_246, temp_287);
    // 0x000870: 0x59A1168000070B2D Ffma
    temp_302 = -temp_256;
    temp_303 = fma(temp_47, temp_302, temp_294);
    // 0x000878: 0x5C68100000670303 Fmul
    temp_304 = temp_64 * temp_51;
    // 0x000888: 0xEFF07F800F07FF18 Ast
    out.outAttr7.x = temp_288;
    // 0x000890: 0x59A10D0000570B18 Ffma
    temp_305 = -temp_246;
    temp_306 = fma(temp_47, temp_305, temp_166);
    // 0x000898: 0xEFF07F800F47FF2F Ast
    out.outAttr7.y = temp_296;
    // 0x0008A8: 0x5C68100000B7061A Fmul
    temp_307 = temp_51 * temp_47;
    // 0x0008B0: 0x59A0010000770C05 Ffma
    temp_308 = fma(temp_70, temp_254, temp_273);
    // 0x0008B8: 0x59A0158000170C06 Ffma
    temp_309 = fma(temp_70, temp_255, temp_300);
    // 0x0008C8: 0x59A00C0000170B18 Ffma
    temp_310 = fma(temp_47, temp_255, temp_306);
    // 0x0008D0: 0x59A00B8001A70C17 Ffma
    temp_311 = fma(temp_70, temp_307, temp_233);
    // 0x0008D8: 0x59A117000017080B Ffma
    temp_312 = -temp_255;
    temp_313 = fma(temp_16, temp_312, temp_301);
    // 0x0008E8: 0x59A1168000770801 Ffma
    temp_314 = -temp_254;
    temp_315 = fma(temp_16, temp_314, temp_303);
    // 0x0008F0: 0x49A0070C0387080E Ffma
    temp_316 = fma(temp_16, constant_buffers.vp_c3->data[14].x, temp_295);
    // 0x0008F8: 0x49A0130C03B70C26 Ffma
    temp_317 = fma(temp_70, constant_buffers.vp_c3->data[14].w, temp_292);
    // 0x000908: 0x59A00C0000770902 Ffma
    temp_318 = fma(temp_20, temp_254, temp_310);
    // 0x000910: 0x49A00F0C03870C1E Ffma
    temp_319 = fma(temp_70, constant_buffers.vp_c3->data[14].x, temp_289);
    // 0x000918: 0x49A0078C03970C0F Ffma
    temp_320 = fma(temp_70, constant_buffers.vp_c3->data[14].y, temp_276);
    // 0x000928: 0x49A0138C03A70C27 Ffma
    temp_321 = fma(temp_70, constant_buffers.vp_c3->data[14].z, temp_278);
    // 0x000930: 0x59A1028000370C05 Ffma
    temp_322 = -temp_304;
    temp_323 = fma(temp_70, temp_322, temp_308);
    // 0x000938: 0x5C68100000A71718 Fmul
    temp_324 = temp_311 * temp_298;
    // 0x000948: 0x59A10D8001A70D1B Ffma
    temp_325 = -temp_307;
    temp_326 = fma(temp_74, temp_325, temp_234);
    // 0x000950: 0xEFF07F800CC7FF18 Ast
    out.outAttr4.w = temp_324;
    // 0x000958: 0x49A0150C03B7082A Ffma
    temp_327 = fma(temp_16, constant_buffers.vp_c3->data[14].w, temp_284);
    // 0x000968: 0x49A00C8C03970817 Ffma
    temp_328 = fma(temp_16, constant_buffers.vp_c3->data[14].y, temp_285);
    // 0x000970: 0x49A0148C03A7080C Ffma
    temp_329 = fma(temp_16, constant_buffers.vp_c3->data[14].z, temp_297);
    // 0x000978: 0x59A0008000370808 Ffma
    temp_330 = fma(temp_16, temp_304, temp_315);
    // 0x000988: 0xF0C8000001E70001 S2r
    // 0x000990: 0x59A0030000470D06 Ffma
    temp_331 = fma(temp_74, temp_245, temp_309);
    // 0x000998: 0x59A105800047091A Ffma
    temp_332 = -temp_245;
    temp_333 = fma(temp_20, temp_332, temp_313);
    // 0x0009A8: 0x49A0070C03C70904 Ffma
    temp_334 = fma(temp_20, constant_buffers.vp_c3->data[15].x, temp_316);
    // 0x0009B0: 0xF0C8000001F7000E S2r
    // 0x0009B8: 0x59A10A0000770D14 Ffma
    temp_335 = -temp_254;
    temp_336 = fma(temp_74, temp_335, temp_275);
    // 0x0009C8: 0x4C58101000E71212 Fadd
    temp_337 = temp_92 + constant_buffers.vp_c4->data[3].z;
    // 0x0009D0: 0x49A0150C03F70907 Ffma
    temp_338 = fma(temp_20, constant_buffers.vp_c3->data[15].w, temp_327);
    // 0x0009D8: 0x59A101000037090B Ffma
    temp_339 = -temp_304;
    temp_340 = fma(temp_20, temp_339, temp_318);
    // 0x0009E8: 0x49A00B8C03D70917 Ffma
    temp_341 = fma(temp_20, constant_buffers.vp_c3->data[15].y, temp_328);
    // 0x0009F0: 0x49A0060C03E7090C Ffma
    temp_342 = fma(temp_20, constant_buffers.vp_c3->data[15].z, temp_329);
    // 0x0009F8: 0x59A00A0000370D14 Ffma
    temp_343 = fma(temp_74, temp_304, temp_336);
    // 0x000A08: 0x59A00D0000070909 Ffma
    temp_344 = fma(temp_20, temp_256, temp_333);
    // 0x000A10: 0x59A1030000070D03 Ffma
    temp_345 = -temp_256;
    temp_346 = fma(temp_74, temp_345, temp_331);
    // 0x000A18: 0x59A00A8001270707 Ffma
    temp_347 = fma(temp_338, temp_337, temp_186);
    // 0x000A28: 0x4C58101000F71600 Fadd
    temp_348 = temp_87 + constant_buffers.vp_c4->data[3].w;
    // 0x000A30: 0x49A0130C03F70D26 Ffma
    temp_349 = fma(temp_74, constant_buffers.vp_c3->data[15].w, temp_317);
    // 0x000A38: 0x59A0098000471213 Ffma
    temp_350 = fma(temp_337, temp_334, temp_191);
    // 0x000A48: 0x49A00F0C03C70D1E Ffma
    temp_351 = fma(temp_74, constant_buffers.vp_c3->data[15].x, temp_319);
    // 0x000A50: 0x49A0078C03D70D0F Ffma
    temp_352 = fma(temp_74, constant_buffers.vp_c3->data[15].y, temp_320);
    // 0x000A58: 0x59A0080001771202 Ffma
    temp_353 = fma(temp_337, temp_341, temp_196);
    // 0x000A68: 0x49A0138C03E70D0D Ffma
    temp_354 = fma(temp_74, constant_buffers.vp_c3->data[15].z, temp_321);
    // 0x000A70: 0x59A0038000072607 Ffma
    temp_355 = fma(temp_349, temp_348, temp_347);
    // 0x000A78: 0x59A0088001270C12 Ffma
    temp_356 = fma(temp_342, temp_337, temp_197);
    // 0x000A88: 0xEFF07F8008C7FF07 Ast
    out.outAttr0.w = temp_355;
    // 0x000A90: 0x59A0098001E70013 Ffma
    temp_357 = fma(temp_348, temp_351, temp_350);
    // 0x000A98: 0x59A0010000F70002 Ffma
    temp_358 = fma(temp_348, temp_352, temp_353);
    // 0x000AA8: 0xEFF07F800807FF13 Ast
    out.outAttr0.x = temp_357;
    // 0x000AB0: 0x5C68100000770101 Fmul
    // 0x000AB8: 0xEFF07F800847FF02 Ast
    out.outAttr0.y = temp_358;
    // 0x000AC8: 0x59A0090000070D00 Ffma
    temp_359 = fma(temp_354, temp_348, temp_356);
    // 0x000AD0: 0x5C68100000770E0E Fmul
    // 0x000AD8: 0xEFF07F800887FF00 Ast
    out.outAttr0.z = temp_359;
    // 0x000AE8: 0x5BB9838001370107 Fsetp
    // 0x000AF0: 0x5BB983800137014F Fsetp
    // 0x000AF8: 0x5BB9838000270117 Fsetp
    // 0x000B08: 0x5BB983800027015F Fsetp
    // 0x000B10: 0x5BB983800FF70737 Fsetp
    // 0x000B18: 0x5BB9838000070E27 Fsetp
    // 0x000B28: 0x5BB9838000070E6F Fsetp
    // 0x000B30: 0x5C68100000A71C1C Fmul
    temp_360 = temp_237 * temp_298;
    // 0x000B38: 0x5C68100000A71D1D Fmul
    temp_361 = temp_244 * temp_298;
    // 0x000B48: 0xEFF07F800B07FF1C Ast
    out.outAttr3.x = temp_360;
    // 0x000B50: 0x5C68100000A71F1F Fmul
    temp_362 = temp_264 * temp_298;
    // 0x000B58: 0xEFF07F800C07FF1D Ast
    out.outAttr4.x = temp_361;
    // 0x000B68: 0x5C68100000A72222 Fmul
    temp_363 = temp_262 * temp_298;
    // 0x000B70: 0xEFF07F800D07FF1F Ast
    out.outAttr5.x = temp_362;
    // 0x000B78: 0x5C68100000A72323 Fmul
    temp_364 = temp_248 * temp_298;
    // 0x000B88: 0xEFF07F800B47FF22 Ast
    out.outAttr3.y = temp_363;
    // 0x000B90: 0x5C68100000A72424 Fmul
    temp_365 = temp_249 * temp_298;
    // 0x000B98: 0xEFF07F800BC7FF23 Ast
    out.outAttr3.w = temp_364;
    // 0x000BA8: 0x5C68100000A72525 Fmul
    temp_366 = temp_250 * temp_298;
    // 0x000BB0: 0xEFF07F800B87FF24 Ast
    out.outAttr3.z = temp_365;
    // 0x000BB8: 0x5C68100000A71B1B Fmul
    temp_367 = temp_326 * temp_298;
    // 0x000BC8: 0xEFF07F800C47FF25 Ast
    out.outAttr4.y = temp_366;
    // 0x000BD0: 0x5C68100000A70505 Fmul
    temp_368 = temp_323 * temp_298;
    // 0x000BD8: 0xEFF07F800C87FF1B Ast
    out.outAttr4.z = temp_367;
    // 0x000BE8: 0x5C68100000A70808 Fmul
    temp_369 = temp_330 * temp_298;
    // 0x000BF0: 0xEFF07F800DC7FF05 Ast
    out.outAttr5.w = temp_368;
    // 0x000BF8: 0x5C68100000A71414 Fmul
    temp_370 = temp_343 * temp_298;
    // 0x000C08: 0xEFF07F800EC7FF08 Ast
    out.outAttr6.w = temp_369;
    // 0x000C10: 0x5C68100000A70B0B Fmul
    temp_371 = temp_340 * temp_298;
    // 0x000C18: 0xEFF07F800D87FF14 Ast
    out.outAttr5.z = temp_370;
    // 0x000C28: 0x5C68100000A70303 Fmul
    temp_372 = temp_346 * temp_298;
    // 0x000C30: 0xEFF07F800E87FF0B Ast
    out.outAttr6.z = temp_371;
    // 0x000C38: 0x5C68100000A70909 Fmul
    temp_373 = temp_344 * temp_298;
    // 0x000C48: 0xEFF07F800D47FF03 Ast
    out.outAttr5.y = temp_372;
    // 0x000C50: 0x0103F8000007F02C Mov32i
    // 0x000C58: 0xEFF07F800E47FF09 Ast
    out.outAttr6.y = temp_373;
    // 0x000C68: 0x5C68100000A7210A Fmul
    temp_374 = temp_266 * temp_298;
    // 0x000C70: 0xEFF07F800907FF2C Ast
    out.outAttr1.x = 1.0f;
    // 0x000C78: 0x50E2432111170000 Votevtg
    // 0x000C88: 0xEFF07F800947FF2C Ast
    out.outAttr1.y = 1.0f;
    // 0x000C90: 0xEFF07F800987FF2C Ast
    out.outAttr1.z = 1.0f;
    // 0x000C98: 0xEFF07F8009C7FF2C Ast
    out.outAttr1.w = 1.0f;
    // 0x000CA8: 0xEFF07F800E07FF0A Ast
    out.outAttr6.x = temp_374;
    // 0x000CB0: 0xE30000000007001C Exit
    // 0x000CB8: 0xEFF07F800707FF13 Ast
    out.position.x = temp_357;
    // 0x000CC8: 0xEFF07F800747FF02 Ast
    out.position.y = temp_358;
    // 0x000CD0: 0xEFF07F800787FF00 Ast
    out.position.z = temp_359;
    // 0x000CD8: 0xEFF07F8007C7FF07 Ast
    out.position.w = temp_355;
    // 0x000CE8: 0xEFF07F800687FF28 Ast
    // 0x000CF0: 0xE30000000007000F Exit
    return couch_transport_v1_exit(out);
    return couch_transport_v1_exit(out);
}
