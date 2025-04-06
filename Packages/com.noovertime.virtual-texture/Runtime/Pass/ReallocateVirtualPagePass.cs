using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

namespace NoOvertime.VirtualTexture
{
    public class ReallocateVirtualPagePass : CustomPass, IDisposable
    {
        private ProfilerMarker _reallocateVirtualPageMarker;
        private readonly List<KeyValuePair<int2, int4>> _delayRemoveSectors;
        private readonly Dictionary<int2, (int4, int4)> _delayRemoveImages;
        private readonly Stack<int3> _travelStack;
        private readonly List<int2> _requestSectors;
        private int2 _lastCameraSector;
        private float2 _lastCameraPositionXZ;

#if UNITY_EDITOR && DEBUG_TERRAIN && DEBUG_EXTRA
        private readonly Dictionary<int2, GameObject> _debugImage = new();
        private readonly GameObject _canvas;
        private readonly GameObject _imagePrefab;
#endif

        public ReallocateVirtualPagePass(ProfilerMarker reallocateVirtualPageMarker)
        {
            _reallocateVirtualPageMarker = reallocateVirtualPageMarker;
            _delayRemoveSectors = new List<KeyValuePair<int2, int4>>();
            _delayRemoveImages = new Dictionary<int2, (int4, int4)>();
            _travelStack = new Stack<int3>();
            _requestSectors = new List<int2>(Constant.MaxPreloadSector);
            _lastCameraSector = new int2(int.MinValue, int.MinValue);
            _lastCameraPositionXZ = new float2(float.MinValue, float.MinValue);

#if UNITY_EDITOR && DEBUG_TERRAIN && DEBUG_EXTRA
            var canvasPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SampleScene/Canvas.prefab");
            _canvas = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(canvasPrefab);
            _canvas.SetActive(false);
            _imagePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SampleScene/Image.prefab");
#endif
        }

        /// <summary>
        /// ReallocateVirtualPagePass的核心逻辑是：
        /// - 基于相机位置生成Sector
        /// - 基于Sector维护indirectTex的位置和大小，并对Sector产生变化的部分升采样/降采样
        /// - 在一个32x32的R32_UINT纹理中，记录每个Sector的indirectTex偏移量/大小
        /// </summary>
        /// <param name="ctx"></param>
        protected override void Execute(CustomPassContext ctx)
        {
            using (_reallocateVirtualPageMarker.Auto())
            {
                Vector3 cameraPosition = ctx.hdCamera.camera.transform.position;

                // 当相机所处的sector发生变化时
                if (CenterSectorChanged(cameraPosition))
                {
                    // 做全局四叉树遍历，加载对应的Sector
                    UpdateRequestSectors(ctx.cmd);
                }

                if (CameraPositionChanged(cameraPosition))
                {
                    // 做几件事。
                    // 1. 维护各种全局数据结构，比如AllSectors，ImageInfo2Sector，ImageInfo，IndirectionTexture等
                    // 2. indirectTex升采样/降采样（基于CS）
                    // 3. 遍历本帧所有的sector，将对应indirectTex的偏移/大小 encode，并用于更新一个32x32的R32_UINT纹理
                    // 【这个32x32 r32纹理 并没有出现在任何paper中？】
                    ReallocateVirtualImage(ctx.cmd);
                }

#if UNITY_EDITOR && DEBUG_TERRAIN&& DEBUG_EXTRA
                ShowThisFrameSectors();
#endif
            }
        }

#if UNITY_EDITOR && DEBUG_TERRAIN && DEBUG_EXTRA
        private void ShowThisFrameSectors()
        {
            foreach (var (sector, instance) in _debugImage)
            {
                instance.SetActive(false);
            }

            foreach (var sector in _requestSectors)
            {
                // 调试模式下显示颜色
                if (!_debugImage.TryGetValue(sector, out var instance))
                {
                    instance = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(_imagePrefab, _canvas.transform);
                    //instance.hideFlags = HideFlags.HideAndDontSave;
                    _debugImage[sector] = instance;
                }

                int2 relativeSize = Context.Instance.TerrainSize >> Constant.SectorSizeShift;
                Vector2 sectorUV = new Vector2(sector.x / (float)relativeSize.x, sector.y / (float)relativeSize.y);
                sectorUV *= 1024;

                var rectTransform = instance.GetComponent<RectTransform>();
                instance.SetActive(true);
                rectTransform.position = new Vector3(sectorUV.x, sectorUV.y);
                rectTransform.sizeDelta = new Vector2(30, 30);
            }
        }
#endif


        public void Dispose()
        {
            _lastCameraSector = new int2(int.MinValue, int.MinValue);
            _lastCameraPositionXZ = new float2(float.MinValue, float.MinValue);
        }

        #region StreamingSectors

        private bool CenterSectorChanged(in float3 cameraPosition)
        {
            var cameraSector = (int2)cameraPosition.xz >> Constant.SectorSizeShift;
            if (cameraSector.x == _lastCameraSector.x && cameraSector.y == _lastCameraSector.y) return false;
            _lastCameraSector = cameraSector;
            return true;
        }

        /// <summary>
        /// step 1. 从整个地形作为一个大块开始，如果一个块太远，就不加载；如果接近但还不是最小块，就把它拆成四个子块继续判断；如果是最小块，就加载到 _requestSectors。
        /// step 2. 用 _requestSectors 和历史Sector（Context.Instance.AllSectors）做比对，将历史中无用的Sector放到 _delayRemoveSectors 中，后续将移除。
        /// step 3. 从全局（Context.Instance）相关若干成员变量中，将这些Sector的信息统统移除
        /// step 4. 将要加载的 _requestSectors 的 Sector 加载到全局相关的成员变量中
        /// </summary>
        /// <param name="cmd"></param>
        private void UpdateRequestSectors(CommandBuffer cmd)
        {
            // step 1. 从整个地形作为一个大块开始，如果一个块太远，就不加载；如果接近但还不是最小块，就把它拆成四个子块继续判断；如果是最小块，就加载到 _requestSectors。

            // x, z, size
            _requestSectors.Clear();
            _travelStack.Push(new int3(0, 0, Context.Instance.TerrainSize >> Constant.SectorSizeShift)); // 2048 >> 6 = 32

            while (_travelStack.TryPop(out var currentNode))
            {
                int halfSize = currentNode.z >> 1;

                // 进到这个方法时，_lastCameraSector已经是当前帧相机的位置
                // 1. 基于块和相机的距离判断，如果区域的距离够小且靠近，并且是一个Sector（最小块）
                // 2. 最终提交的只有最小块（currentNode.z == 1），也就是Sector
                var sqrDistance = math.lengthsq(currentNode.xy - _lastCameraSector + halfSize);
                if (currentNode.z == 1) // 已经到最小级别了, 不要再往下遍历
                {
                    // 如果是最小块
                    if (sqrDistance < Constant.SectorPreloadDistance * Constant.SectorPreloadDistance) // sector load范围大概是256+32(区块边缘到中心)
                    {
                        _requestSectors.Add(new int2(currentNode.x, currentNode.y));
                    }
                }
                else
                {
                    if (sqrDistance < math.lengthsq(new float2(halfSize + Constant.SectorPreloadDistance, halfSize + Constant.SectorPreloadDistance)))
                    {
                        // 例如，如果 split from 0, 0, 32....
                        _travelStack.Push(new int3(currentNode.x + halfSize, currentNode.y + halfSize, halfSize)); // 16, 16, 16
                        _travelStack.Push(new int3(currentNode.x, currentNode.y + halfSize, halfSize)); // 16, 0, 16
                        _travelStack.Push(new int3(currentNode.x + halfSize, currentNode.y, halfSize)); // 0, 16, 16
                        _travelStack.Push(new int3(currentNode.x, currentNode.y, halfSize)); // 0, 0, 16
                    }
                }
            }

            // step 2. 用 _requestSectors 和历史Sector（Context.Instance.AllSectors）做比对，将历史中无用的Sector放到 _delayRemoveSectors 中，后续将移除。
            _delayRemoveSectors.Clear();
            foreach (var pair in Context.Instance.AllSectors)
            {
                if (!_requestSectors.Contains(pair.Key))
                {
                    _delayRemoveSectors.Add(pair);
                }
            }

            // step 3. 从全局（Context.Instance）相关若干成员变量中，将 _delayRemoveSectors 统统移除
            foreach (var (removeSector, imageInfo) in _delayRemoveSectors)
            {
                Context.Instance.AllSectors.Remove(removeSector);
                Context.Instance.VirtualImageAtlas.RemoveImage(removeSector);
                Context.Instance.ImageInfo2Sector.Remove(imageInfo.yzw); // yz = indirectXZ像素偏移量；w=indirectTex像素大小
                Context.Instance.ImageInfo.Remove(imageInfo.yzw);
                if (imageInfo.w > 0)
                {
                    Context.Instance.IndirectionTexture.RemoveVirtualImage(cmd, imageInfo);
                }
            }

            // step 4. 将要加载的 _requestSectors 的 Sector 加载到全局相关的成员变量中
            foreach (var sector in _requestSectors)
            {
                if (Context.Instance.AllSectors.ContainsKey(sector)) continue;
                Context.Instance.AllSectors.Add(sector, int4.zero);
            }
        }

        #endregion

        #region AllocateVirtualImage

        private bool CameraPositionChanged(float3 cameraPosition)
        {
            float2 cameraPositionXZ = cameraPosition.xz;
            var sqrPositionDelta = math.lengthsq(_lastCameraPositionXZ - cameraPositionXZ);
            if (sqrPositionDelta < Constant.CameraPositionSqrDeltaThreshold) return false;
            _lastCameraPositionXZ = cameraPositionXZ;
            return true;
        }

        private void ReallocateVirtualImage(CommandBuffer cmd)
        {
            _delayRemoveImages.Clear();

            // AllSector的key=具体的sector，value=记录了四叉树节点，indirectXZ偏移量，还有indirectXZ的大小
            foreach (var (sector, oldImageInfo) in Context.Instance.AllSectors)
            {
                // 遍历所有Sector，基于相机位置计算出新的VirtualImage大小
                int virtualImageSize = Utility.CalculateTargetImageSize(sector.xy * 64 + 32, _lastCameraPositionXZ);
                int oldVirtualImageSize = oldImageInfo.w << Constant.PageSizeShift; 

                // 如果VirtualImage有变化
                if (virtualImageSize != oldVirtualImageSize) 
                {
                    // 按照AVT规则，先在Atlas（IndirectTex）中插入，再移除
                    // 这里是插入。
                    // 插入的结果会返回imageInfo：x对应四叉树节点，yz=在indirectXZ中的偏移量，w=indirectXZ的大小
                    Context.Instance.VirtualImageAtlas.InsertImage(sector, virtualImageSize, out var imageInfo);

                    // 最后记录一下新老imageInfo；老数据后续用于移除；新数据用于移除后重建，见下面。
                    _delayRemoveImages[sector] = (oldImageInfo, imageInfo); 
#if DEBUG_TERRAIN
                    UnityEngine.Assertions.Assert.AreNotEqual(imageInfo.w, 0, $"InsertImage of sector:{sector}, size:{virtualImageSize} failed.");
#if UNITY_EDITOR
                    Debug.Log($"lizha @ {Time.frameCount} {sector} change from {oldVirtualImageSize} to {virtualImageSize}");
#endif
#endif
                }
            }

            // 新老imageInfo；老数据后续用于移除；新数据用于移除后重建。
            foreach (var (sector, (oldImageInfo, newImageInfo)) in _delayRemoveImages)
            {
                Context.Instance.ImageInfo2Sector.Remove(oldImageInfo.yzw);
                Context.Instance.ImageInfo.Remove(oldImageInfo.yzw);
                Context.Instance.AllSectors.Remove(sector);
                if (newImageInfo.w > 0)
                {
                    Context.Instance.ImageInfo2Sector.Add(newImageInfo.yzw, sector);
                    Context.Instance.ImageInfo.Add(newImageInfo.yzw);
                    Context.Instance.AllSectors.Add(sector, newImageInfo);
                }

                if (oldImageInfo.w > 0)
                {
                    // indirectTex在此进行升/降采样迁移操作
                    Context.Instance.IndirectionTexture.RemapVirtualImage(cmd, oldImageInfo, newImageInfo);
                }

                Context.Instance.VirtualImageAtlas.RemoveImage(oldImageInfo);
            }

            foreach (var (sector, imageInfo) in Context.Instance.AllSectors)
            {
                // 最大是log2(65536/256(pageSize)) = 8, 最小是log2(1024(minimalVirtualImageSize)/256) = 2
                // (1024 texel/cm is mip 0)
                var virtualPageSizeLog = math.ceillog2(imageInfo.w);
                // 12bit virtual page x, 12bit virtual page z, 8bit virtual page log2(size)
                uint encoded = ((uint)imageInfo.y << 20) + ((uint)imageInfo.z << 8) + (uint)virtualPageSizeLog;

                // 遍历本帧所有的sector，将对应indirectTex的偏移/大小 encode，并用于更新一个32x32的R32_UINT纹理
                Context.Instance.Sector2VirtualImageInfoTexture.UpdateSector(sector, encoded);
            }

            // 遍历本帧所有的sector，将对应indirectTex的偏移/大小 encode，并用于更新一个32x32的R32_UINT纹理
            Context.Instance.Sector2VirtualImageInfoTexture.UpdateSector2VirtualImageInfo(cmd);
        }

        #endregion
    }
}