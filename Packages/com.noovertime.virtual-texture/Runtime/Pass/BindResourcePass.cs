using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NoOvertime.VirtualTexture
{
    public class BindResourcePass : CustomPass
    {
        protected override void Execute(CustomPassContext ctx)
        {
            // Sector2VirtualImageTexture
            // xy = sector的坐标；z = 虚拟纹理信息（12bit X + 12bit Y + 8bit Size, for indirect Texture）
            ctx.cmd.SetGlobalTexture(Constant.Sector2VirtualImageInfoTextureID, Context.Instance.Sector2VirtualImageInfoTexture.RT);

            // IndirectTexture
            ctx.cmd.SetGlobalTexture(Constant.IndirectionTextureID, Context.Instance.IndirectionTexture.RT);

            // BaseMapAtlas【？】
            ctx.cmd.SetGlobalTexture(Constant.PhysicalPageBaseMapAtlasID, Context.Instance.PhysicalPageAtlas.PhysicalPageBaseMapAtlas);

            // NormalMapAtlas【？】
            ctx.cmd.SetGlobalTexture(Constant.PhysicalPageMaskMapAtlasID, Context.Instance.PhysicalPageAtlas.PhysicalPageMaskMapAtlas);

            ctx.cmd.EnableKeyword(Context.Instance.VirtualTextureKeyword);
        }
    }
}