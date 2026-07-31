// Co-coded with DeepSeek V4 Pro (preview, thinking) in AstrBot

using System.IO;
using UnityEditor;

public static class ExportUnityPackage
{
    public static void Export()
    {
        string packagePath = "Assets/Editor/VibingTools";
        string exportDir  = "Build";

        if (!Directory.Exists(exportDir))
            Directory.CreateDirectory(exportDir);

        AssetDatabase.Refresh();

        AssetDatabase.ExportPackage(
            packagePath,
            Path.Combine(exportDir, "VibingTools.unitypackage"),
            ExportPackageOptions.Recurse
        );

        UnityEngine.Debug.Log(
            $"Exported: {Path.GetFullPath(Path.Combine(exportDir, "VibingTools.unitypackage"))}"
        );
    }
}
