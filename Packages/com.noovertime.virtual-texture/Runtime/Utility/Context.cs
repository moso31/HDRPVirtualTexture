using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace NoOvertime.VirtualTexture
{
    public class Context : IDisposable
    {
        public int TerrainSize;
        public Resource Resource;
        public RuntimeResource RuntimeResource;

        // 存放所有的VirtualImage信息
        public VirtualImageAtlas VirtualImageAtlas;

        // 存储历史（通常是上一帧）的所有Sector的信息。
        // int2 = Sector 的坐标
        // int4：x = nodeIndex, yz = xz, w = sector的size
        public Dictionary<int2, int4> AllSectors;

        // 记录indirect Tex上page信息到sector的反映射
        // int3: xy = indirectionPageXZ = 间接纹理坐标；z = virtualPageSize = 虚拟页大小
        public Dictionary<int3, int2> ImageInfo2Sector;
        public NativeHashSet<int3> ImageInfo; // 和 ImageInfo2Sector 相似

        public IndirectionTexture IndirectionTexture;
        public PhysicalPageAtlas PhysicalPageAtlas;
        public Sector2VirtualImageInfoTexture Sector2VirtualImageInfoTexture;

        // Feedback回读纹理及其数据
        public RTHandle PageIDOutputTexture;
        public NativeArray<uint> ReadBackArray;

        public NativeList<uint> ResolvedPageID;
        public GlobalKeyword VirtualTextureKeyword;
        public GlobalKeyword OutputPageIDTextureKeyword;
        public static readonly Context Instance = new Context();
        private bool _initialized;

        private Context()
        {
        }

        public void Initialize(int terrainSize, Resource resource, RuntimeResource runtimeResource)
        {
            Assert.AreEqual(_initialized, false);
            _initialized = true;
            Resource = resource;
            RuntimeResource = runtimeResource;
            TerrainSize = terrainSize;
            VirtualImageAtlas = new VirtualImageAtlas(Constant.IndirectionTextureSize, Constant.PageSize, Constant.PageSizeShift, Constant.MinimalVirtualImageSize);
            AllSectors = new Dictionary<int2, int4>();
            ImageInfo2Sector = new Dictionary<int3, int2>();
            ImageInfo = new NativeHashSet<int3>(128, Allocator.Persistent);
            IndirectionTexture = new IndirectionTexture(Constant.IndirectionTextureSize);
            PhysicalPageAtlas = new PhysicalPageAtlas();
            var sectorCount = terrainSize >> Constant.SectorSizeShift; // 2048 >> 6 = 32，即这个地形上有多少个Sector
            Sector2VirtualImageInfoTexture = new Sector2VirtualImageInfoTexture(sectorCount);
            ResolvedPageID = new NativeList<uint>(Constant.MaxDeduplicatedPageCount, Allocator.Persistent);
            VirtualTextureKeyword = GlobalKeyword.Create("_VIRTUAL_TEXTURE");
            OutputPageIDTextureKeyword = GlobalKeyword.Create("_OUTPUT_PAGE_ID_TEXTURE");
        }

        public void Dispose()
        {
            Assert.AreEqual(_initialized, true);
            VirtualImageAtlas.Dispose();
            AllSectors.Clear();
            ImageInfo2Sector.Clear();
            ImageInfo.Dispose();
            IndirectionTexture.Dispose();
            PhysicalPageAtlas.Dispose();
            Sector2VirtualImageInfoTexture.Dispose();
            ResolvedPageID.Dispose();
            _initialized = false;
        }
    }
}