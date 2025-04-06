using System.Collections.Generic;
using Unity.Burst;
using Unity.Mathematics;

namespace NoOvertime.VirtualTexture
{
    public static class Utility
    {
        /// <summary>
        /// virtual page X, virtual page Y, mip, log2(virtual page size)
        /// </summary>
        [BurstCompile]
        public static int4 UnpackPageID(uint packedPageID)
        {
            // 0-12bit: indirect Tex X
            // 12-24bit: indirect Tex Y
            return new int4((int)(packedPageID >> 20), (int)((packedPageID >> 8) & 0xFFF), (int)((packedPageID >> 4) & 0xF), (int)(packedPageID & 0xF));
        }

        public static ulong EncodeLRUKey(in int2 sector, in int2 localPageID, int mip, int virtualImageSizeLog)
        {
            // 这里设计了一个"虚坐标"的概念：
            // 看运行时的Canvas就知道 VirtImage Mip0的大小不一定是256*256。这里假设了三点：
            // 1. 将VirtImage强制拉伸到256*256的大小（和ChenKa PPT 64k*64k/sector 的精度差的挺多，但Demo嘛~）
            // 2. 依然设定每个sector都位于正确的世界坐标，但每个坐标填充对应的VirtImage，由此形成新的精度坐标，即虚坐标。
            // 3. 假设"虚坐标"是有mip的

            // 基于上面的设定，获取实际的"虚坐标"，这里原作者写的是"virtualPageMip0"，一个意思
            // 虚坐标的最小单位依然是texel，所以 int2。
            int2 virtualPageMip0 = Constant.MaxVirtualPageSize * sector + (localPageID << (Constant.MaxVirtualPageSizeShift - virtualImageSizeLog));

            // 获取"虚坐标"下这个sector对应的GPU Mip等级（如果VirtImage真实大小没达到256，这里就会相对原GPU mip有偏移）
            int physicalMip = (Constant.MaxVirtualPageSizeShift - virtualImageSizeLog + mip);

            // 如上述，"虚坐标"有mip。virtualPageID负责调整到这一mip。
            int2 virtualPageID = virtualPageMip0 >> physicalMip; 

            // 这个坐标仍然处于虚坐标空间，所以范围和精度相比localPageID更大一些 32bit放不下，改64bit
            // 8bit empty, 24bit x, 24bit y, 8bit mip
            return ((ulong)virtualPageID.x << 32) + ((ulong)virtualPageID.y << 8) + (ulong)physicalMip;
        }

        public static (int2, int) DecodeLRUKey(ulong lruKey)
        {
            int2 virtualPageID;
            virtualPageID.x = (int)(lruKey >> 32) & 0xFFFFFF;
            virtualPageID.y = (int)(lruKey >> 8) & 0xFFFFFF;
            int physicalMip = (int)(lruKey) & 0xFF;
            return (virtualPageID, physicalMip);
        }

        /// <summary>
        /// 基于sector和相机的距离 计算virtualImageSize的等级
        /// </summary>
        /// <param name="sectorPosition"></param>
        /// <param name="cameraPosition"></param>
        /// <returns>返回这个sector对应的VirtualImage大小</returns>
        public static int CalculateTargetImageSize(in float2 sectorPosition, in float2 cameraPosition)
        {
            // 距离越远，lod越高
            float distance = math.lengthsq((sectorPosition - cameraPosition));
            float t = (distance / Constant.SwitchDistance);
            int lodImage = 0;
            if (t >= 1)
            {
                lodImage = (int)math.log2(t) + 1;
            }

            int virtualImageSize = Constant.HighestResolution >> lodImage; // = 65536 >> lodImage
            return virtualImageSize;
        }
    }

    public class LinkedListNodeCache<T>
    {
        private int _nodesCreated = 0;
        private LinkedList<T> _nodeCache;

        /// <summary>
        /// Creates or returns a LinkedListNode of the requested type and set the value.
        /// </summary>
        /// <param name="val">The value to set to returned node to.</param>
        /// <returns>A LinkedListNode with the value set to val.</returns>
        public LinkedListNode<T> Acquire(T val)
        {
            if (_nodeCache != null)
            {
                var n = _nodeCache.First;
                if (n != null)
                {
                    _nodeCache.RemoveFirst();
                    n.Value = val;
                    return n;
                }
            }

            _nodesCreated++;
            return new LinkedListNode<T>(val);
        }

        /// <summary>
        /// Release the linked list node for later use.
        /// </summary>
        /// <param name="node"></param>
        public void Release(LinkedListNode<T> node)
        {
            if (_nodeCache == null)
                _nodeCache = new LinkedList<T>();

            node.Value = default(T);
            _nodeCache.AddLast(node);
        }

        internal int CreatedNodeCount => _nodesCreated;

        internal int CachedNodeCount
        {
            get => _nodeCache == null ? 0 : _nodeCache.Count;
            set
            {
                if (_nodeCache == null)
                    _nodeCache = new LinkedList<T>();
                while (value < _nodeCache.Count)
                    _nodeCache.RemoveLast();
                while (value > _nodeCache.Count)
                    _nodeCache.AddLast(new LinkedListNode<T>(default));
            }
        }
    }
}