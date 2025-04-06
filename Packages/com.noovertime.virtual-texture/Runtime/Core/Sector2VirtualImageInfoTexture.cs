using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace NoOvertime.VirtualTexture
{
    public class Sector2VirtualImageInfoTexture : IDisposable
    {
        // 下面两个数据大小为256个uint3，映射了同一数据，一个CPU一个GPU
        // uint3 的 xy 表示 sector 的坐标，z 表示 sector 中的虚拟图像的 32bit 编码()
        private readonly uint3[] _updateSector2VirtualImageInfoArray = new uint3[Constant.MaxPreloadSector]; // 存储在CPU
        private readonly ComputeBuffer _write2AtlasBuffer; // 存储在GPU

        // 一张大小32x32的R32_UINT的纹理，32是全地形sector的数量。
        // 所以这里实际上每个像素映射了一个区域对应的VirtualImageInfo
        private readonly RTHandle _sector2VirtualImageInfo;

        private int _updateSector2VirtualImageInfoCount;
        private readonly int _clearThreadGroups;

        public Sector2VirtualImageInfoTexture(int sectorCount)
        {
            // sectorCount = 32; // 2048 >> 6 = 32
            _sector2VirtualImageInfo = RTHandles.Alloc(sectorCount, sectorCount, colorFormat: GraphicsFormat.R32_UInt, enableRandomWrite: true);

            _write2AtlasBuffer = new ComputeBuffer(Constant.MaxPreloadSector, 12, ComputeBufferType.Structured, ComputeBufferMode.Immutable); // 见上 成员变量说明

            _clearThreadGroups = sectorCount / 8 + 1; // = 5
        }

        public void Dispose()
        {
            RTHandles.Release(_sector2VirtualImageInfo);
            _write2AtlasBuffer.Dispose();
        }
        
        // 对外接口，当外面调这个RT的时候，实际就是这个32x32的R32_UINT纹理。
        public RTHandle RT => _sector2VirtualImageInfo;

        // 先在这个方法中记录当前帧的encoded
        // 全部记录完成后交给UpdateSector2VirtualImageInfo()，以写入到实际GPU中(indirect offset x:12, indirect offset y : 12, indirect size: 8)
        public void UpdateSector(int2 sector, uint encoded)
        {
            _updateSector2VirtualImageInfoArray[_updateSector2VirtualImageInfoCount++] = new uint3((uint2)sector, encoded);
        }

        public void UpdateSector2VirtualImageInfo(CommandBuffer cmd)
        {
            // clear，先将 32x32的R32_UINT的纹理 全部清零
            cmd.SetComputeTextureParam(Context.Instance.Resource.clearSector2VirtualImageInfoTextureCompute, 0, Constant.RWSector2VirtualImageInfoTextureID, _sector2VirtualImageInfo);

            // 分成5x5的线程组，内部每个线程组8x8，实际40x40，可以完全覆盖32x32
            cmd.DispatchCompute(Context.Instance.Resource.clearSector2VirtualImageInfoTextureCompute, 0, _clearThreadGroups, _clearThreadGroups, 1);

            // update，然后将_updateSector2VirtualImageInfoArray的数据注入到 32x32的R32_UINT 中
            cmd.SetGlobalInteger(Constant.Write2AtlasCountID, _updateSector2VirtualImageInfoCount); // bufferSize
            cmd.SetComputeBufferParam(Context.Instance.Resource.updateSector2VirtualImageInfoTextureCompute, 0, Constant.Write2AtlasBufferID, _write2AtlasBuffer);
            cmd.SetBufferData(_write2AtlasBuffer, _updateSector2VirtualImageInfoArray);
            cmd.SetComputeTextureParam(Context.Instance.Resource.updateSector2VirtualImageInfoTextureCompute, 0, Constant.RWSector2VirtualImageInfoTextureID, _sector2VirtualImageInfo); // 32x32的R32_UINT
            cmd.DispatchCompute(Context.Instance.Resource.updateSector2VirtualImageInfoTextureCompute, 0, 16, 1, 1);
            _updateSector2VirtualImageInfoCount = 0;
        }
    }
}