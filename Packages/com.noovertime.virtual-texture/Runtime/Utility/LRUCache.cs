using System.Collections.Generic;

namespace NoOvertime.VirtualTexture
{
    // 一个最近最少使用（LRU）缓存的实现
    // 核心思想是总是优先删除最少使用的元素

    // 在这个VTDemo中，只有一种用法 LRUCache<ulong, uint>。
    // ulong是一个64位的整数，表示一个虚拟页面的ID
    //      64位的key由sector、indirect Tex Mip0 下 sector 内局部偏移量（localPageID）、当前像素在GPU上的mip、当前像素的Max mip value构成

    public class LRUCache<TKey, TValue>
    {
        private readonly int _capacity; // 缓存size
        // 主字典。
        // 之所以弄个cache，是为了优化访问速度到O(1)平均（C# Dictionary 基于哈希表+list/红黑树）
        private readonly Dictionary<TKey, (LinkedListNode<TKey> node, TValue value)> _cache; 
        private readonly LinkedList<TKey> _list;
        private readonly LinkedListNodeCache<TKey> _nodeCache;

        public LRUCache(int capacity)
        {
            _capacity = capacity;
            _cache = new Dictionary<TKey, (LinkedListNode<TKey> node, TValue value)>(capacity);
            _list = new LinkedList<TKey>();
            _nodeCache = new LinkedListNodeCache<TKey>();
        }

        // 尝试获取链表中的key，如果链表中没有key就返回false
        public bool Touch(TKey key, out TValue value)
        {
            if (!_cache.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }

            // 如果在链表中找到node，将其移除并重新添加。
            // 这是LRU的核心，目的是 使新增的node总是放在链表头部
            value = node.value;
            _list.Remove(node.node);
            _list.AddFirst(node.node);
            return true;
        }

        public bool RemoveLast(out TKey key, out TValue value)
        {
            // 移除一个位于链表末尾的元素

            if (_list.Count > 0)
            {
                var last = _list.Last;
                TKey removeKey = last.Value;
                key = removeKey;
                value = _cache[removeKey].value;
                _cache.Remove(removeKey);
                _list.RemoveLast();
                _nodeCache.Release(last);
                return true;
            }

            key = default;
            value = default;
            return false;
        }

        // 在链表头部插入一个<key,value>
        public bool Insert(TKey key, TValue value)
        {
            if (_cache.ContainsKey(key)) return false;
            if (_cache.Count >= _capacity) return false;
            var node = _nodeCache.Acquire(key);
            _list.AddFirst(node);
            _cache.Add(key, (node, value));
            return true;
        }
    }
}