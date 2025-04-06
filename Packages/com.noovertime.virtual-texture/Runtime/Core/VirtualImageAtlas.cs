using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NoOvertime.VirtualTexture
{
    /// <summary>
    /// 在 RVT 中，VirtualImageAtlas 相当于 IndirectTex 上大小灵活可变的纹理
    /// </summary>
    public class VirtualImageAtlas : IDisposable
    {
        // 整个indirect texture的大小 = 1024.
        private readonly int _atlasSize; 

        // 页的大小。1 << _pageSizeShift == _pageSize【谁的页？】
        private readonly int _pageSizeShift; 

        private readonly bool[] _markAsUsed; // 记录atlas四叉树中的节点是否被占用
        private readonly byte[] _markChildAsUsed; // 记录atlas四叉树中的节点中是否有子节点被占用

        // 最小的virtual image size。默认=2048px
        private readonly int _minimalVirtualImageSize;

        // x = nodeIndex = 四叉树线性nodeId；
        // yz = 当前四叉树page偏移量 = indirectTex像素偏移量；
        // w = 当前四叉树节点等级对应的page大小=indirectTex像素大小；
        private readonly Stack<int4> _travelStack = new();

        // 记录atlas中所有image所在的节点index和x,z坐标以及imageSize
        // int2 = sector 的坐标
        // int4 = nodeIndex, x, z, size（注意这里在概念上 记录的是VirtualImageAtlas区域偏移量）
        private readonly Dictionary<int2, int4> _sector2ImageDictionary = new();

#if UNITY_EDITOR && DEBUG_TERRAIN
        private readonly Dictionary<int2, GameObject> _debugImage = new();
        private readonly GameObject _canvas;
        private readonly GameObject _imagePrefab;
#endif

        public VirtualImageAtlas(int atlasSize, int pageSize, int pageSizeShift, int minimalVirtualImageSize) // 1024, 256, 8, 2048
        {
            _atlasSize = atlasSize;
#if UNITY_EDITOR && DEBUG_TERRAIN
            var isPow2 = math.ispow2(pageSize);
            if (!isPow2)
            {
                throw new ArgumentException("Page Size 必须是2的次幂.");
            }

            if (1 << pageSizeShift != pageSize)
            {
                throw new ArgumentException("Page Size 和 Page Size Shift不匹配.");
            }

            var canvasPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SampleScene/Canvas.prefab");
            _canvas = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(canvasPrefab);
            //_canvas.hideFlags = HideFlags.HideAndDontSave;
            _imagePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SampleScene/Image.prefab");
#endif
            _pageSizeShift = pageSizeShift;
            _minimalVirtualImageSize = minimalVirtualImageSize;

            // 计算四叉树节点的数量
            var nodeCount = 0;
            int currentSizeNodeCount = 1;
            int currentNodeSize = atlasSize; // 获取IndirectTexture的大小=1024.

            // 将IndirectTex逐级四叉分割，直到分割到最小的VirtualImageSize大小
            // 注：IndirectTex的基本单位是page，而VirtualImageSize的基本单位是texel，所以需要用pageSizeShift转换
            while (currentNodeSize > (minimalVirtualImageSize >> pageSizeShift)) // 最小的VirtImage = 2048 >> 8 = 8 个 page
            {
                nodeCount += currentSizeNodeCount; // 统计这一层的节点数量
                currentNodeSize >>= 1; // 大小减半
                currentSizeNodeCount <<= 2; // 节点数量乘4
            }

            // 按 page 记录 整个四叉树的父节点们的状态，共 5461 个
            _markChildAsUsed = new byte[nodeCount]; // 父节点标记用不到最小级别的image

            // 按 page 记录 整个四叉树的所有节点（父+子节点）的状态，共 5461 + 16384 = 21845 个
            _markAsUsed = new bool[nodeCount + currentSizeNodeCount];
        }

        public void Dispose()
        {
#if UNITY_EDITOR && DEBUG_TERRAIN
            foreach (var (_, instance) in _debugImage)
            {
                Object.DestroyImmediate(instance);
            }

            _debugImage.Clear();
            Object.DestroyImmediate(_canvas);
#endif
        }

        /// <summary>
        /// 在 Indirect Texture 中插入一个 Virtual Image
        /// </summary>
        /// <param name="sector">sector的位置。一个VirtualImage总是对应一个sector。</param>
        /// <param name="virtualImageSize">VirtualImage的大小，单位为texel</param>
        /// <param name="imageInfo">返回值，x = nodeIndex = 当前sector对应的四叉树线性nodeId；yz = 当前四叉树page偏移量 = 当前sector对应的indirectTex mip0中的像素偏移量；w=当前sector对应的indirectTex mip0中的像素大小</param>
        /// <returns></returns>
        public bool InsertImage(in int2 sector, in int virtualImageSize, out int4 imageInfo)
        {
            if (virtualImageSize < _minimalVirtualImageSize)
            {
#if UNITY_EDITOR && DEBUG_TERRAIN
                Debug.LogWarning($"lizha @ {sector} request {virtualImageSize} < minimal({_minimalVirtualImageSize})");
#endif
                imageInfo = int4.zero;
                return false;
            }

            // VirtualImageSize换算成page大小，即indirectTexture中所占的像素大小
            var indirectSize = virtualImageSize >> _pageSizeShift;


            _travelStack.Clear();
            // 四叉树在概念上对标indirectTexture；这里首先Push根节点。
            // x = nodeIndex = 四叉树线性nodeId
            // yz = 当前四叉树page偏移量 = indirectTex像素偏移量
            // w=当前四叉树节点等级对应的page大小=indirectTex像素大小
            _travelStack.Push(new int4(0, 0, 0, _atlasSize)); 

            // 下面的while负责：从四叉树中找出一个没有被占用的节点，并记录下来（记录到_sector2ImageDictionary）
            while (_travelStack.TryPop(out var currentNode))
            {
                // 如果node被占用了, 那么node的子节点肯定也被占用了
                if (_markAsUsed[currentNode.x]) continue;

                // 如果节点范围比VirtualImage大，递归搜子节点
                if (currentNode.w > indirectSize)
                {
                    int halfSize = currentNode.w >> 1; // 子节点，大小减半
                    int childNodeIndex = currentNode.x << 2; // 子节点，index*4
                    _travelStack.Push(new int4(childNodeIndex + 4, currentNode.y + halfSize, currentNode.z + halfSize, halfSize));
                    _travelStack.Push(new int4(childNodeIndex + 3, currentNode.y, currentNode.z + halfSize, halfSize));
                    _travelStack.Push(new int4(childNodeIndex + 2, currentNode.y + halfSize, currentNode.z, halfSize));
                    _travelStack.Push(new int4(childNodeIndex + 1, currentNode.y, currentNode.z, halfSize));
                }
                else 
                {
                    // 如果节点范围刚好等于VirtualImage，或者内部子节点是干净的（没有被使用过的）
                    if (virtualImageSize == _minimalVirtualImageSize || _markChildAsUsed[currentNode.x] == 0)
                    {
                        _markAsUsed[currentNode.x] = true; // 标记自身被占用
                        int parent = currentNode.x >> 2; // 向上逐级通报所有父节点，子节点占用数+1
                        while (parent != 0)
                        {
                            _markChildAsUsed[parent]++;
                            parent >>= 2;
                        }

                        // 最后正式分配节点。记录当前节点信息
                        _sector2ImageDictionary[sector] = currentNode;
                        imageInfo = currentNode;

#if UNITY_EDITOR && DEBUG_TERRAIN 
                        // 调试模式下显示颜色
                        if (!_debugImage.TryGetValue(sector, out var instance))
                        {
                            instance = (GameObject) UnityEditor.PrefabUtility.InstantiatePrefab(_imagePrefab, _canvas.transform);
                            //instance.hideFlags = HideFlags.HideAndDontSave;
                            _debugImage[sector] = instance;
                        }

                        var rectTransform = instance.GetComponent<RectTransform>();
                        instance.SetActive(true);
                        rectTransform.position = new Vector3(imageInfo.y + 1, imageInfo.z + 1);
                        rectTransform.sizeDelta = new Vector2(imageInfo.w - 2, imageInfo.w - 2);
                        var image = instance.GetComponent<Image>();
                        var color = Color.HSVToRGB(math.log2(imageInfo.w) / 8, 1, 1);
                        color.a = 0.5f;
                        image.color = color;
#endif
                        return true;
                    }
                }
            }

            imageInfo = int4.zero;
            return false; // atlas 满了?
        }

        /// <summary>
        /// VirtualImageReallocate以后用于移除老的占用部分
        /// </summary>
        public void RemoveImage(in int4 imageInfo)
        {
            _markAsUsed[imageInfo.x] = false;
            int parent = imageInfo.x >> 2; // 标记所有父节点中有子节点被占用减一
            while (parent != 0)
            {
                _markChildAsUsed[parent]--;
                parent >>= 2;
            }
        }

        /// <summary>
        /// 从VirtualImageAtlas中移除整个Sector
        /// </summary>
        public bool RemoveImage(in int2 sector)
        {
            if (_sector2ImageDictionary.TryGetValue(sector, out var imageInfo))
            {
                RemoveImage(imageInfo);
                _sector2ImageDictionary.Remove(sector);
#if UNITY_EDITOR && DEBUG_TERRAIN
                _debugImage[sector].SetActive(false);
#endif
            }

            return false;
        }
    }
}