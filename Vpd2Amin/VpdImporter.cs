using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace VpdToAnim
{
    /// <summary>
    /// Lets Unity import .vpd files directly as TextAsset,
    /// so you can drag a pose file straight into the Project window.
    /// The text is decoded from Shift-JIS once here; consumers use TextAsset.text.
    /// </summary>
    [ScriptedImporter(1, "vpd")]
    public class VpdImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var text = VpdPose.Decode(File.ReadAllBytes(ctx.assetPath));
            var asset = new TextAsset(text) { name = Path.GetFileNameWithoutExtension(ctx.assetPath) };
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);
        }
    }
}
