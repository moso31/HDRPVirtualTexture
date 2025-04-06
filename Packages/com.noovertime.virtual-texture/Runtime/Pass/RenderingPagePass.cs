using System;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering.HighDefinition;

namespace NoOvertime.VirtualTexture
{
    /// <summary>
    /// 因为unity terrain使用的整张的splat map而不是分sector的
    /// BakePhysicalPage.compute需要获得world position
    /// </summary>
    public class RenderingPagePass : CustomPass, IDisposable
    {
        private ProfilerMarker _renderingPageMarker;
        private readonly LRUCache<ulong, int> _lruCache = new(Constant.MaxPhysicalPageCount);
        private int _pageIndex;

        public RenderingPagePass(ProfilerMarker renderingPageMarker)
        {
            _renderingPageMarker = renderingPageMarker;
            for (int i = 0; i < Constant.MaxPhysicalPageCount; i++)
            {
                // 默认填了很多的无用值
                _lruCache.Insert(ulong.MaxValue - (ulong) i, i);
            }
        }

        public void Dispose()
        {
        }

        public void StartRendering()
        {
            _pageIndex = 0;
#if UNITY_EDITOR && DEBUG_TERRAIN
            Debug.Log($"lizha @ {Time.frameCount} Page count: {Context.Instance.ResolvedPageID.Length}");
#endif
        }

        public bool IsRenderingFinished()
        {
            // ResolvedPageID 最多可以存储 255 个 pageID
            // pageID 存储的信息：xy = indirect Tex XY, z = gpu mip（如果是来自Feedback）或 indirectTex maxMipValue（如果并不来自Feedback）, w = indirectTex maxMipValue.
            return _pageIndex >= Context.Instance.ResolvedPageID.Length;
        }

        protected override void Execute(CustomPassContext ctx)
        {
            using (_renderingPageMarker.Auto())
            {
                int renderingCount = 0;
                // 【限制条件？】
                while (!IsRenderingFinished() && renderingCount < Constant.RenderingPagePerFrame && Context.Instance.IndirectionTexture.IsUpdateArrayFarFromFull())
                {
                    // 取出一个pageID，并解码
                    uint packedPageID = Context.Instance.ResolvedPageID[_pageIndex++];
                    int4 pageID = Utility.UnpackPageID(packedPageID);

                    // 解析出当前Page对应的 indirectTex Mip 0 对应大小（单位为 indirectTex Texel）
                    int virtualPageSize = 1 << pageID.w;

                    // 这里先 << z，得到当前page在indirectTex mip0下的大小；
                    // 然后右移 w 再左移 w，即可抹掉余数，得到当前page在indirectTex mip0下的起始位置
                    int2 indirectionPageXZ = ((pageID.xy << pageID.z) >> pageID.w) << pageID.w;

                    // 当前page在indirect Tex mip0的（绝对位置 - 起始位置）= 在sector内的相对位置
                    int2 localPageID = (pageID.xy << pageID.z) - indirectionPageXZ;

                    // ImageInfo2Sector 记录了 indirect Tex上page信息到sector的反映射，所以可拿到sector的坐标
                    // 计算得到sector后, 通过偏移sector即可得到真实的世界空间virtual image uv
                    bool getSector = Context.Instance.ImageInfo2Sector.TryGetValue(new int3(indirectionPageXZ, virtualPageSize), out var sector);
                    Assert.IsTrue(getSector);

                    // 编码成LRUkey。
                    // 内部有一个"虚坐标"的概念，假设全部sector都填充了最大分辨率的page，并记录page在该空间下的坐标(lruKey.xy)和mip(lruKey.z)。
                    ulong lruKey = Utility.EncodeLRUKey(sector, localPageID, pageID.z, pageID.w);

                    // 先看下LruCache里是否存有此page，如果有会自动移到lruCache的头部
                    // slot 记录了 PhysicalPageAtlas上 Tex2DArray 的索引，并且是固定的初始化for int i = i（见构造函数）
                    bool contains = _lruCache.Touch(lruKey, out var slot); 

                    // 如果有，continue
                    if (!contains)
                    {
                        // 如果没有，看下容量满了没(预分配了无效的page, 一定是满的)
                        // 满了的话需要移除一个slot出来给新page用
                        // 另外需要注意一下sector image size的降低可能需要清理mip0 key
                        // 这里有个问题:
                        // sector virtual image size降低的时候，indirection上的原有mip0会被抹掉
                        // 但是LRUCache没法移除，这样即使physical page还存活的情况下indirection entry没了
                        // 当virtual image size降低后立即回升，需要重新建立indirection entry
                        // 暂时没什么好想法，只能每帧都更新indirection texture?

                        // 总是移除LRU的最后一个元素
                        // cache默认是被填满的并且有很多无效的lrukey
                        bool removeLast = _lruCache.RemoveLast(out var removeLRUKey, out slot);

                        // 如果移除的 是一个有效的LRUKey
                        if (removeLRUKey < 0x7FFFFFFFFFFFFFFFu) 
                        {
                            // 解码出"虚坐标"的pageID和mip
                            var (virtualPageID, physicalMip) = Utility.DecodeLRUKey(removeLRUKey);

                            // 根据page大小反推出sector坐标
                            int2 removeSector = (virtualPageID << physicalMip) / Constant.MaxVirtualPageSize;

                            // 获取当前帧的相机sector，看看是否是本帧相机看到的sector
                            bool getRemoveSectorInfo = Context.Instance.AllSectors.TryGetValue(removeSector, out var imageInfo);
                            bool isSectorValid = imageInfo.w > 0;

                            // 如果是本帧sector的话那就是比较糟糕的情况了（结合LRU touch自动提表头的逻辑，其实很难走进这里）
                            // 因为能走到这里意味着当前要加载的page，在LRU中正要被移除
                            if (getRemoveSectorInfo && isSectorValid)
                            {
                                int2 localVirtualPageID = (virtualPageID << physicalMip) % Constant.MaxVirtualPageSize;
                                int4 removePageID = int4.zero;
                                int virtualPageSizeLog = math.ceillog2(imageInfo.w);
                                int mip = physicalMip - Constant.MaxVirtualPageSizeShift + virtualPageSizeLog;
                                mip = math.max(mip, 0);
                                removePageID.xy = (imageInfo.yz >> mip) + (localVirtualPageID >> physicalMip);
                                removePageID.z = mip;
                                removePageID.w = virtualPageSizeLog;

                                // 手动将removePageID提交给IndirectTex，让其重新更新
                                Context.Instance.IndirectionTexture.AddUpdateArray(removePageID, 65535);
                            }
                        }

                        // 如果之前LRUCache没有，就需要新增。首先插入到cache
                        Assert.IsTrue(removeLast, "LRU Cache怎么搞成空的了");
                        bool insert = _lruCache.Insert(lruKey, slot);
                        Assert.IsTrue(insert, "LRU Cache插入失败");

                        // 然后更新PhysicalPageAtlas纹理。
                        Context.Instance.PhysicalPageAtlas.Render(ctx.cmd, sector, localPageID, pageID, slot);
                        renderingCount++;
                    }

                    // xy是indirection texture的uv, z是mip, w是physical page的index
                    Context.Instance.IndirectionTexture.AddUpdateArray(pageID, slot);
                }

                Context.Instance.IndirectionTexture.UpdateIndirectionTexture(ctx.cmd);
            }
        }
    }
}