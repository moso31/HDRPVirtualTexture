#ifndef VIRTUAL_TEXTURE_INCLUDED
#define VIRTUAL_TEXTURE_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "ShaderConstant.cs.hlsl"

RWTexture2D<uint> PageIDOutputTexture : register(u7);
// Sector2VirtualImageInfoTexture 记录了大世界Sector的VirtImageInfo之间的映射
Texture2D<uint> Sector2VirtualImageInfoTexture;
Texture2D<uint> IndirectionTexture;
Texture2DArray<float4> PhysicalPageBaseMapAtlas;
Texture2DArray<float4> PhysicalPageMaskMapAtlas;
uint VirtualDitherX;
uint VirtualDitherY;
SamplerState sampler_linear_clamp_aniso8;

// From https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm
float MipLevelAnisotropy(float2 uv, float size)
{
    float2 dX = ddx(uv * size);
    float2 dY = ddy(uv * size);
    float squaredLengthX = dot(dX, dX);
    float squaredLengthY = dot(dY, dY);
    float determinant = abs(dX.x*dY.y - dX.y*dY.x);
    bool isMajorX = squaredLengthX > squaredLengthY;
    float squaredLengthMajor = isMajorX ? squaredLengthX : squaredLengthY;
    float lengthMajor = sqrt(squaredLengthMajor);
    float normMajor = 1.f/lengthMajor;

    float2 anisoLineDirection;
    anisoLineDirection.x = (isMajorX ? dX.x : dY.x) * normMajor;
    anisoLineDirection.y = (isMajorX ? dX.y : dY.y) * normMajor;

    float ratioOfAnisotropy = squaredLengthMajor/determinant;

    // clamp ratio and compute LOD
    float lengthMinor;
    const float maxAniso = 8;
    if ( ratioOfAnisotropy > maxAniso ) // maxAniso comes from a Sampler state.
    {
        // ratio is clamped - LOD is based on ratio (preserves area)
        ratioOfAnisotropy = maxAniso;
        lengthMinor = lengthMajor/ratioOfAnisotropy;
    }
    else
    {
        // ratio not clamped - LOD is based on area
        lengthMinor = determinant/lengthMajor;
    }

    // clamp to top LOD
    if (lengthMinor < 1.0)
    {
        ratioOfAnisotropy = max( 1.0, ratioOfAnisotropy*lengthMinor );

        // lengthMinor = 1.0 // This line is no longer recommended for future hardware
        //
        // The commented out line above was part of the D3D10 spec until 8/17/2009,
        // when it was finally noticed that it was undesirable.
        //
        // Consider the case when the LOD is negative (lengthMinor less than 1),
        // but a positive LOD bias will be applied later on due to
        // sampler / instruction settings.
        //
        // With the clamp of lengthMinor above, the log2() below would make a
        // negative LOD become 0, after which any LOD biasing would apply later.
        // That means with biasing, LOD values less than the bias amount are
        // unavailable.  This would look blurrier than isotropic filtering,
        // which is obviously incorrect.  The output of this routine must allow
        // negative LOD values, so that LOD bias (if used) can still result in
        // hitting the most detailed mip levels.
        //
        // Because this issue was only noticed years after the D3D10 spec was originally
        // authored, many implementations will include a clamp such as commented out
        // above.  WHQL must therefore allow implementations that support either
        // behavior - clamping or not.  It is recommended that future hardware
        // does not do the clamp to 1.0 (thus allowing negative LOD).
        // The same applies for D3D11 hardware as well, since even the D3D11 specs
        // had already been locked down for a long time before this issue was uncovered.
    }

    float LOD = log2(lengthMinor);
    return LOD;
}

uint2 GetVirtualPageID(float3 positionWS, out float mip, out uint virtualPageSizeLog)
{
    // 读取 sector2VirtImage 纹理，基于sector索引
    const uint packedImageInfo = LOAD_TEXTURE2D(Sector2VirtualImageInfoTexture, positionWS.xz / 64);  // 世界坐标/64 就是 sector 的坐标

    // 解码，读出imageinfo
    uint3 imageInfo = uint3(packedImageInfo >> 20, (packedImageInfo >> 8) & 0xFFF, packedImageInfo & 0xF);
    virtualPageSizeLog = imageInfo.z;

    // 基于当前像素的ddxy，推导一个GPU mip等级
    // note：这里推导的Mip等级 和 sector2VirtImage 在当前区域存储的mip等级（即virtualPageSizeLog） 并不一致
    //      前者是当前像素的GPU mip；后者是当前像素-sector对应的indirectTex mip0的大小
    mip = MipLevelAnisotropy(positionWS.xz, MAX_TEXEL_DENSITY) - MAX_VIRTUAL_PAGE_SIZE_SHIFT + virtualPageSizeLog;
    mip = clamp(mip, 0, virtualPageSizeLog);

    // 获取世界坐标在sector内的相对位置
    const float2 sectorPosition = positionWS.xz % SECTOR_SIZE;

    // 基于相对位置，推导出在 VirtImage 上的相对坐标
    // 这里虽然写的是UV，但实际范围是
    const uint2 virtualImageUV = ((uint2)(sectorPosition * (1 << virtualPageSizeLog))) >> SECTOR_SIZE_SHIFT; 

    // 再加上imageinfo的偏移，然后>>到指定mip
    uint2 virtualPageID = (virtualImageUV + imageInfo.xy) >> ((uint)mip);

    // 这样就得到了在indirectTex上的坐标（int2=像素位置，即uv*indirectTexSize）
    // note：注意这里 virtualPageID 返回的是 ** indirectTex 当前mip ** 等级的坐标，而不是indirectTex的mip0坐标
    return virtualPageID;
}

bool MatchMipLevel(uint2 virtualPageID, int virtualPageSizeLog, inout int mip, out int slot)
{
    UNITY_UNROLLX(MAX_VIRTUAL_PAGE_SIZE_SHIFT)
    while (true)
    {
        // 在indirectTex上直接采样。如 UpdateIndirectionTexture.compute 中所示，最终将返回PhysicalPageAtlas Tex2DArray的Index
        slot = LOAD_TEXTURE2D_LOD(IndirectionTexture, virtualPageID, mip);
        if (slot < MAX_PHYSICAL_PAGE_COUNT) return true; // 如果找到对应的PhysPage就直接返回

        // 如果没有找到，继续进一步寻找子mip
        virtualPageID = virtualPageID >> 1;
        mip++;
        if (mip > virtualPageSizeLog) break;
    }
    return false;
}

bool SampleVT(float3 positionRWS, out float4 baseMap, out float4 maskMap)
{
    float3 positionWS = GetAbsolutePositionWS(positionRWS); 
    int mip;
    uint virtualPageSizeLog;
    // 读当前位置的virtualPageID.
    // virtualPageID: indirectTex mip i（当前像素对应mip等级）的坐标
    // mip: indirectTex GPU mip
    // virtualPageSizeLog: indirectTex mipCount（也可视作mip0的大小）
    const uint2 virtualPageID = GetVirtualPageID(positionWS, mip, virtualPageSizeLog); 
    int slot;

    // 寻找对应的PhysicalPage Tex2DArray slot
    const bool match = MatchMipLevel(virtualPageID, virtualPageSizeLog, mip, slot);

    // 如果找到slot，基于世界坐标和slot进行采样，计算出最终的baseMap和maskMap颜色
    if (match)
    {
        const uint texelPerMeter = 1 << virtualPageSizeLog - mip;
        const float2 sectorPosition = frac(positionWS.xz * INV_SECTOR_SIZE * texelPerMeter);
        float2 uvInPhysicalPage = (sectorPosition * PAGE_SIZE + 4.0f) * INV_PAGE_SIZE_WITH_BORDER;
        baseMap = SAMPLE_TEXTURE2D_ARRAY_LOD(PhysicalPageBaseMapAtlas, sampler_linear_clamp_aniso8, uvInPhysicalPage, slot, 0);
        maskMap = SAMPLE_TEXTURE2D_ARRAY_LOD(PhysicalPageMaskMapAtlas, sampler_linear_clamp_aniso8, uvInPhysicalPage, slot, 0);
        // debug border/mip
        // if ((uvInPhysicalPage * 264.f).x < 4.f || (uvInPhysicalPage * 264.f).x > 256.f)
        // {
        //     baseMap = 0;
        // }
        // if ((uvInPhysicalPage * 264.f).y < 4.f || (uvInPhysicalPage * 264.f).y > 256.f)
        // {
        //     baseMap = 0;
        // }
    }
    else
    {
        baseMap = 0;
        maskMap = 0;
    }
    return match;
}

void OutputPageID(float3 positionWS, uint2 positionSS)
{
    uint mip, virtualPageSizeLog;

    // 获取virtualPageID  = indirectTex mip i（当前像素对应mip等级）的坐标，同时也记录当前像素mip=mip，当前像素对应的indirectTex mipCount=virtualPageSizeLog （也可视作mip0的大小）
    uint2 virtualPageID = GetVirtualPageID(positionWS, mip, virtualPageSizeLog);

    // 重新encode
    // 这次encode的数据就是“活的”（由GPU返回得到的）。
    const uint packed = (virtualPageID.x << 20) + (virtualPageID.y << 8) + ((mip & 0xF) << 4) + virtualPageSizeLog;

    uint2 downscaleSS = positionSS % PAGE_ID_DOWNSCALE; // 获取屏幕空间坐标，基于DitherXY（详见FeedbackPass）抖动采样
    if (downscaleSS.x == VirtualDitherX && downscaleSS.y == VirtualDitherY)
    {
        // 【为啥会有个virtualPageSizeLog == 0的条件？如果mip == 0，岂不是没法正常工作？】
        if (virtualPageSizeLog == 0 || mip > virtualPageSizeLog)
        {
            // virtualPageSizeLog == i 的时候 说明对应sector在indirect Tex的mip0上只占i*i像素
            // 这种情况下，mip不可能>i，因为indirect Tex的mip i+1等级及以上根本无法表达这个sector（小于1px）。
            
            // 所以如果 mip > virtualPageSizeLog 实际上不会拿到任何有效值。
            PageIDOutputTexture[positionSS.xy / PAGE_ID_DOWNSCALE] = 0;
        }
        else 
        {
            // 更新 Feedback 回读 RT，大小为1/8分辨率
            // 在这里存储当前像素的packed信息，其格式和Sector2VirtualImageInfoTexture一致
            PageIDOutputTexture[positionSS.xy / PAGE_ID_DOWNSCALE] = packed;
        }
    }

    // PS：这个时间点通常是在GBuffer，在渲染纹理的阶段【有没有可能其他ShaderLab Pass也会触发？】
}
#endif
