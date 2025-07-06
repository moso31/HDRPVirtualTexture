using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace NoOvertime.VirtualTexture
{
    public class IndirectionTexture : IDisposable
    {
        private readonly RTHandle _indirectionTexture;

        // 记录当前帧对indirectTex的写入行为的次数；
        // 对mip0和非mip0等级做了区分【为啥？】
        private readonly int4[] _write2IndirectionTextureArray = new int4[Constant.UpdateIndirectionTexturePerFrame];
        private readonly int4[] _write2IndirectionTextureArrayMip0 = new int4[Constant.UpdateIndirectionTexturePerFrame];
        private int _write2IndirectionCount;
        private int _write2IndirectionMip0Count;

        private readonly ComputeBuffer _write2IndirectionBuffer;
        private readonly ComputeBuffer _paramsBuffer;
        private readonly uint[] _paramsArray = new uint[6];

        public IndirectionTexture(int textureSize)
        {
            // indirectTex本体。大小=1024，格式R16，启用mip，支持随机写入
            _indirectionTexture = RTHandles.Alloc(textureSize, textureSize, 1, DepthBits.None, GraphicsFormat.R16_UInt, useMipMap: true, autoGenerateMips: false, enableRandomWrite: true,
                name: nameof(_indirectionTexture));

            // 分配一个 StructuredBuffer，大小为64*16=1024字节；
            _write2IndirectionBuffer = new ComputeBuffer(Constant.UpdateIndirectionTexturePerFrame, 16, ComputeBufferType.Structured, ComputeBufferMode.Immutable);

            // 分配一个 StructuredBuffer，大小为6*4=24字节；
            _paramsBuffer = new ComputeBuffer(6, 4, ComputeBufferType.Structured, ComputeBufferMode.Immutable);

            // 下面相当于把 indirectTex 各级mip所有值都改成65536u == 0（R16_UInt溢出）
            CommandBuffer cmd = new CommandBuffer();
            for (int i = 0; i < Constant.RWIndirectionTextureIDs.Length; i++) // Length = 9 对应9级indirectTex的mip
            {
                _paramsArray[0] = (uint)(1024 >> i); // 1024, 512, 256, 128, 64, 32, 16, 8, 4
                _paramsArray[2] = 0;
                _paramsArray[3] = 0;
                cmd.SetBufferData(_paramsBuffer, _paramsArray);

                // kernelIndex = 1, so ClearImage()
                cmd.SetComputeBufferParam(Context.Instance.Resource.remapIndirectionTextureCompute, 1, Constant.ParamsID, _paramsBuffer);
                cmd.SetComputeTextureParam(Context.Instance.Resource.remapIndirectionTextureCompute, 1, Constant.RWIndirectionTextureIDs[0], _indirectionTexture, i);
                int threadGroupCount = 1024 >> (2 + i);

                cmd.DispatchCompute(Context.Instance.Resource.remapIndirectionTextureCompute, 1, threadGroupCount, threadGroupCount, 1);
            }

            Graphics.ExecuteCommandBuffer(cmd);
        }

        public void Dispose()
        {
            RTHandles.Release(_indirectionTexture);
            _write2IndirectionBuffer.Dispose();
            _paramsBuffer.Dispose();
        }

        public RTHandle RT => _indirectionTexture;

        public bool IsUpdateArrayFarFromFull()
        {
            // _write2IndirectionCount = 当前帧已经写入了多少次IndirectTex的mip0和非mip0
            // 任意一个都不能超过单帧64次的上限
            return _write2IndirectionCount + 2 < Constant.UpdateIndirectionTexturePerFrame && _write2IndirectionMip0Count + 2 < Constant.UpdateIndirectionTexturePerFrame;
        }

        public void AddUpdateArray(in int4 pageID, in int slot)
        {
            Assert.IsFalse(_write2IndirectionCount >= _write2IndirectionTextureArray.Length);

            // pageID.xy=indirectTex XY
            // pageID.z=gpu mip（如果是来自Feedback）或 indirectTex maxMipValue（如果是完全新建sector）
            // slot = PhysicalPageAtlas Tex2DArray的索引

            if (pageID.z == 0)
            {
                _write2IndirectionTextureArrayMip0[_write2IndirectionMip0Count++] = new int4(pageID.xyz, slot);
            }
            else
            {
                _write2IndirectionTextureArray[_write2IndirectionCount++] = new int4(pageID.xyz, slot);
            }
        }

        public void UpdateIndirectionTexture(CommandBuffer cmd)
        {
            cmd.SetGlobalInteger(Constant.Write2IndirectionCountID, _write2IndirectionMip0Count);
            cmd.SetBufferData(_write2IndirectionBuffer, _write2IndirectionTextureArrayMip0);
            cmd.SetComputeBufferParam(Context.Instance.Resource.updateIndirectionTextureCompute, 0, Constant.Write2IndirectionBufferID, _write2IndirectionBuffer);
            cmd.SetComputeTextureParam(Context.Instance.Resource.updateIndirectionTextureCompute, 0, Constant.RWIndirectionTextureIDs[0], _indirectionTexture, 0);
            cmd.DispatchCompute(Context.Instance.Resource.updateIndirectionTextureCompute, 0, 8, 1, 1);
            _write2IndirectionMip0Count = 0;

            cmd.SetGlobalInteger(Constant.Write2IndirectionCountID, _write2IndirectionCount);
            cmd.SetBufferData(_write2IndirectionBuffer, _write2IndirectionTextureArray);
            cmd.SetComputeBufferParam(Context.Instance.Resource.updateIndirectionTextureCompute, 1, Constant.Write2IndirectionBufferID, _write2IndirectionBuffer);
            for (int mip = 0; mip < Constant.RWIndirectionTextureIDs.Length; mip++)
            {
                cmd.SetComputeTextureParam(Context.Instance.Resource.updateIndirectionTextureCompute, 1, Constant.RWIndirectionTextureIDs[mip], _indirectionTexture, mip);
            }

            cmd.DispatchCompute(Context.Instance.Resource.updateIndirectionTextureCompute, 1, 8, 1, 1);
            _write2IndirectionCount = 0;
        }

        public void RemoveVirtualImage(CommandBuffer cmd, int4 imageInfo)
        {
            int mipCount = math.ceillog2(imageInfo.w);
            for (int i = 0; i < mipCount; i++)
            {
                _paramsArray[0] = (uint)imageInfo.w >> i;
                _paramsArray[2] = (uint)imageInfo.y >> i;
                _paramsArray[3] = (uint)imageInfo.z >> i;
                cmd.SetBufferData(_paramsBuffer, _paramsArray);
                cmd.SetComputeBufferParam(Context.Instance.Resource.remapIndirectionTextureCompute, 1, Constant.ParamsID, _paramsBuffer);
                cmd.SetComputeTextureParam(Context.Instance.Resource.remapIndirectionTextureCompute, 1, Constant.RWIndirectionTextureIDs[0], _indirectionTexture, i);
                int clearThreadGroups = math.max(imageInfo.w >> (2 + i), 1);
                cmd.DispatchCompute(Context.Instance.Resource.remapIndirectionTextureCompute, 1, clearThreadGroups, clearThreadGroups, 1);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void RemapVirtualImage(CommandBuffer cmd, int4 oldImageInfo, int4 newImageInfo)
        {
            // 无论升还是降，本质都是在indirectTex上做迁移
            // indirectTex中原本就有低分辨率的数据（oldImageInfo），所以可以通过这种迁移来实现快速移动到indirectTex的新位置（newImageInfo）

            // note:
            // 升采样直接copy mip 0-N，但会导致mip0缺失，后续会通过插值和延迟加载填充
            // 降采样则不再需要mip0，所以可以看到DownScale中，先clear mip0再copy mip 1-N

            Assert.AreNotEqual(oldImageInfo.w, newImageInfo.w);
            if (oldImageInfo.w < newImageInfo.w) 
            {
                // 如果新的VirtualImage比旧的大，那么升采样
                Upscale(cmd, oldImageInfo, newImageInfo);
            }
            else 
            {
                // 如果新的VirtualImage比旧的小，那么降采样
                Downscale(cmd, oldImageInfo, newImageInfo);
            }
        }

        private void Upscale(CommandBuffer cmd, int4 oldImageInfo, int4 newImageInfo)
        {
            Assert.IsTrue(oldImageInfo.w < newImageInfo.w);
            int mipCount = math.ceillog2(oldImageInfo.w); // 获取之前indirectTex大小
            int mipDelta = math.ceillog2(newImageInfo.w) - mipCount; // 获取新旧indirectTex大小的差值

            // 遍历之前已有的各级mip，并将纹理数据迁移到新纹理上
            // 迁移完成后将老纹理置0
            for (int mip = 0; mip <= mipCount; mip++)
            {
                _paramsArray[0] = (uint)oldImageInfo.w >> mip; // 当前mip等级 indirectTex大小
                // 当前mip等级 indirectTex偏移xy
                _paramsArray[2] = (uint)oldImageInfo.y >> mip; 
                _paramsArray[3] = (uint)oldImageInfo.z >> mip;
                // Upscale后，新mip indirectionTex偏移xy
                _paramsArray[4] = (uint)newImageInfo.y >> (mip + mipDelta);
                _paramsArray[5] = (uint)newImageInfo.z >> (mip + mipDelta);

                cmd.SetBufferData(_paramsBuffer, _paramsArray);
                cmd.SetComputeBufferParam(Context.Instance.Resource.remapIndirectionTextureCompute, 0, Constant.ParamsID, _paramsBuffer);
                cmd.SetComputeTextureParam(Context.Instance.Resource.remapIndirectionTextureCompute, 0, Constant.RWIndirectionTextureIDs[0], _indirectionTexture, mip);
                cmd.SetComputeTextureParam(Context.Instance.Resource.remapIndirectionTextureCompute, 0, Constant.RWIndirectionTextureIDs[1], _indirectionTexture, mip + mipDelta);

                // 线程组大小是当前mip等级的1/4
                int threadGroups = math.max(oldImageInfo.w >> mip + 2, 1);
                cmd.DispatchCompute(Context.Instance.Resource.remapIndirectionTextureCompute, 0, threadGroups, threadGroups, 1);
            }
        }

        private void Downscale(CommandBuffer cmd, int4 oldImageInfo, int4 newImageInfo)
        {
            // 降采样
            Assert.IsTrue(oldImageInfo.w > newImageInfo.w);
            int mipCount = math.ceillog2(newImageInfo.w);
            int mipDelta = math.ceillog2(oldImageInfo.w) - mipCount;

            // 较高等级的像素将被直接清零（反正也会被后续的mip覆盖）
            for (int mip = 0; mip < mipDelta; mip++)
            {
                _paramsArray[0] = (uint)oldImageInfo.w >> mip;
                _paramsArray[2] = (uint)oldImageInfo.y >> mip;
                _paramsArray[3] = (uint)oldImageInfo.z >> mip;
                cmd.SetBufferData(_paramsBuffer, _paramsArray);
                cmd.SetComputeBufferParam(Context.Instance.Resource.remapIndirectionTextureCompute, 1, Constant.ParamsID, _paramsBuffer);
                cmd.SetComputeTextureParam(Context.Instance.Resource.remapIndirectionTextureCompute, 1, Constant.RWIndirectionTextureIDs[0], _indirectionTexture, 0);
                int clearThreadGroups = math.max(oldImageInfo.w >> mip + 2, 1);
                cmd.DispatchCompute(Context.Instance.Resource.remapIndirectionTextureCompute, 1, clearThreadGroups, clearThreadGroups, 1);
            }

            // 遍历之前已有的各级mip，并将纹理数据迁移到新纹理上
            // 迁移完成后将老纹理置0
            for (int mip = 0; mip <= mipCount; mip++)   
            {
                _paramsArray[0] = (uint)newImageInfo.w >> mip;
                _paramsArray[2] = (uint)oldImageInfo.y >> (mip + mipDelta);
                _paramsArray[3] = (uint)oldImageInfo.z >> (mip + mipDelta);
                _paramsArray[4] = (uint)newImageInfo.y >> mip;
                _paramsArray[5] = (uint)newImageInfo.z >> mip;
                cmd.SetBufferData(_paramsBuffer, _paramsArray);
                cmd.SetComputeBufferParam(Context.Instance.Resource.remapIndirectionTextureCompute, 0, Constant.ParamsID, _paramsBuffer);
                cmd.SetComputeTextureParam(Context.Instance.Resource.remapIndirectionTextureCompute, 0, Constant.RWIndirectionTextureIDs[0], _indirectionTexture, mip + mipDelta);
                cmd.SetComputeTextureParam(Context.Instance.Resource.remapIndirectionTextureCompute, 0, Constant.RWIndirectionTextureIDs[1], _indirectionTexture, mip);
                int threadGroups = math.max(newImageInfo.w >> mip + 2, 1);
                cmd.DispatchCompute(Context.Instance.Resource.remapIndirectionTextureCompute, 0, threadGroups, threadGroups, 1);
            }
        }
    }
}