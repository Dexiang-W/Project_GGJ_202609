using UnityEditor;
using UnityEngine;

// VAT 资产导入设置
//   · VAT_Position.exr：数据贴图 → 关 sRGB / 关 mipmap / Point / Clamp / 半精度
//   · VAT_Water.fbx：只留网格，不导入自带材质与动画
//   · 导入完成后自动把位置贴图挂到 M_NPR_VAT_Water 材质上
public class GGJVatImportSettings : AssetPostprocessor
{
    const string TexPath   = "Assets/0_File_GGJ2026/3_Textures/VAT_Position.exr";
    const string ModelPath = "Assets/0_File_GGJ2026/1_Model/VAT_Water.fbx";
    const string MatPath   = "Assets/0_File_GGJ2026/4_Shader/M_NPR_VAT_Water.mat";

    void OnPreprocessTexture()
    {
        if (!assetPath.Equals(TexPath)) return;

        TextureImporter ti = (TextureImporter)assetImporter;
        ti.textureType         = TextureImporterType.Default;
        ti.sRGBTexture         = false;                 // 存的是坐标数据，绝不能做 sRGB 转换
        ti.alphaIsTransparency = false;
        ti.mipmapEnabled       = false;                 // 关 mip：采样必须精确落在某一行上
        ti.wrapMode            = TextureWrapMode.Clamp;
        ti.filterMode          = FilterMode.Point;
        ti.anisoLevel          = 0;
        ti.maxTextureSize      = 8192;
        ti.streamingMipmaps    = false;

        TextureImporterPlatformSettings ps = new TextureImporterPlatformSettings
        {
            name                 = "Standalone",
            overridden           = true,
            maxTextureSize       = 8192,
            format               = TextureImporterFormat.RGBAHalf,   // 半精度足够，显存省一半
            textureCompression   = TextureImporterCompression.Uncompressed,
            compressionQuality   = 100,
            crunchedCompression  = false,
            allowsAlphaSplitting = false
        };
        ti.SetPlatformTextureSettings(ps);
    }

    void OnPreprocessModel()
    {
        if (!assetPath.Equals(ModelPath)) return;

        ModelImporter mi = (ModelImporter)assetImporter;
        mi.materialImportMode = ModelImporterMaterialImportMode.None;  // 用我们自己的 NPR 材质
        mi.importVisibility   = false;
        mi.importCameras      = false;
        mi.importLights       = false;
        mi.animationType      = ModelImporterAnimationType.None;
        mi.meshCompression    = ModelImporterMeshCompression.Off;
        mi.weldVertices       = false;                                 // 顶点顺序必须和贴图一一对应
        mi.isReadable         = false;
    }

    static void OnPostprocessAllAssets(string[] imported, string[] deleted,
                                       string[] moved, string[] movedFrom)
    {
        foreach (string p in imported)
        {
            if (p == TexPath)
            {
                AssignPositionMap();
                return;
            }
        }
    }

    static void AssignPositionMap()
    {
        Texture  tex = AssetDatabase.LoadAssetAtPath<Texture>(TexPath);
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (tex == null || mat == null) return;
        if (mat.GetTexture("_VatPosMap") == tex) return;

        mat.SetTexture("_VatPosMap", tex);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        Debug.Log("[VAT] 已把 " + TexPath + " 挂到 " + MatPath);
    }

    [MenuItem("GGJ2026/VAT/把位置贴图挂到 VAT 材质")]
    static void AssignPositionMapMenu()
    {
        AssignPositionMap();
    }
}
