using System;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NoOvertime.VirtualTexture
{
    public class FeedBackPass : CustomPass, IDisposable
    {
        private ProfilerMarker _feedBackMarker;
        private int _frameCount;

        public FeedBackPass(ProfilerMarker feedBackMarker)
        {
            _feedBackMarker = feedBackMarker;
        }

        public void Dispose()
        {
            if (Context.Instance.PageIDOutputTexture != null)
            {
                RTHandles.Release(Context.Instance.PageIDOutputTexture);
                Context.Instance.PageIDOutputTexture = null;
            }
        }

        protected override void Execute(CustomPassContext ctx)
        {
            using (_feedBackMarker.Auto())
            {
                // 准备了一个抖动坐标组，64帧一循环
                _frameCount++;
                _frameCount %= 64;
                var dither = Constant.BayerDither8X8[_frameCount / Constant.PageIDTextureDownscale, _frameCount % Constant.PageIDTextureDownscale];

                var referenceSize = ctx.cameraDepthBuffer.referenceSize; // 1920, 1080

                // size = 1/8分辨率，向上取整
                var size = new Vector2Int(
                    (int)(referenceSize.x / (float)Constant.PageIDTextureDownscale) + 1,
                    (int)(referenceSize.y / (float)Constant.PageIDTextureDownscale) + 1); 

                // 重新创建对应纹理
                AllocatePageIDOutputTexture(size);

                // 这里的CS实际只负责Clear
                ctx.cmd.SetComputeTextureParam(Context.Instance.Resource.clearPageIDOutputTextureCompute, 0, Constant.RWPageIDOutputTexture, Context.Instance.PageIDOutputTexture);
                ctx.cmd.DispatchCompute(Context.Instance.Resource.clearPageIDOutputTextureCompute, 0, size.x / 8 + 1, size.y / 8 + 1, 1);

                // 下面的逻辑都和CS无关 // 下面的逻辑都和CS无关 // 下面的逻辑都和CS无关

                // 分辨率缩减至1/8，所以64帧的时间内持续对8x8的区域循环抖动采样
                // 注意这里只是设置了全局Integer，但并没有实际使用
                ctx.cmd.SetGlobalInteger("VirtualDitherX", dither / Constant.PageIDTextureDownscale);
                ctx.cmd.SetGlobalInteger("VirtualDitherY", dither % Constant.PageIDTextureDownscale);

                // 启用定义宏。unity将根据keyword选择shader变体（执行包含这个keyword的版本）
                // 并将回读buffer注册到register(u7)，作为在GBuffer等的输出（详见VirtualTexture.hlsl）
                ctx.cmd.SetRandomWriteTarget(7, Context.Instance.PageIDOutputTexture);
                ctx.cmd.EnableKeyword(Context.Instance.OutputPageIDTextureKeyword);
            }
        }

        private void AllocatePageIDOutputTexture(Vector2Int size)
        {
            if (Context.Instance.PageIDOutputTexture != null && Context.Instance.PageIDOutputTexture.referenceSize != size)
            {
                RTHandles.Release(Context.Instance.PageIDOutputTexture);
                Context.Instance.PageIDOutputTexture = null;
                Context.Instance.ReadBackArray.Dispose();
            }

            if (Context.Instance.PageIDOutputTexture == null)
            {
                Context.Instance.PageIDOutputTexture = RTHandles.Alloc(size.x, size.y, colorFormat: GraphicsFormat.R32_UInt, enableRandomWrite: true, name: nameof(Context.Instance.PageIDOutputTexture));
                Context.Instance.ReadBackArray = new NativeArray<uint>(size.x * size.y, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }
    }
}