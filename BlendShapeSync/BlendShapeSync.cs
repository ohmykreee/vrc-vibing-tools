// Co-coded with DeepSeek V4 Pro (preview, thinking) in Webchat

using UnityEngine;
using UnityEditor;

public class BlendShapeSyncEditor : EditorWindow
{
    public SkinnedMeshRenderer sourceMesh;
    public SkinnedMeshRenderer targetMesh;

    [MenuItem("Tools/Vibing Tools/BlendShape Sync", priority = 10)]
    public static void ShowWindow()
    {
        GetWindow<BlendShapeSyncEditor>("BlendShape 同步器");
    }

    void OnGUI()
    {
        GUILayout.Label("手动同步 BlendShape", EditorStyles.boldLabel);
        sourceMesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("源模型 (高模/身体)", sourceMesh, typeof(SkinnedMeshRenderer), true);
        targetMesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("目标模型 (低模/衣服)", targetMesh, typeof(SkinnedMeshRenderer), true);

        EditorGUI.BeginDisabledGroup(sourceMesh == null || targetMesh == null);
        if (GUILayout.Button("手动同步一次", GUILayout.Height(30)))
        {
            SyncShapes();
        }
        EditorGUI.EndDisabledGroup();
    }

    void SyncShapes()
    {
        Mesh sourceSharedMesh = sourceMesh.sharedMesh;
        Mesh targetSharedMesh = targetMesh.sharedMesh;

        if (sourceSharedMesh == null || targetSharedMesh == null) return;

        for (int i = 0; i < sourceSharedMesh.blendShapeCount; i++)
        {
            string shapeName = sourceSharedMesh.GetBlendShapeName(i);
            int targetIndex = targetSharedMesh.GetBlendShapeIndex(shapeName);

            // 如果两个模型有同名的 BlendShape，则进行同步
            if (targetIndex != -1)
            {
                float weight = sourceMesh.GetBlendShapeWeight(i);
                if (targetMesh.GetBlendShapeWeight(targetIndex) != weight)
                {
                    Undo.RecordObject(targetMesh, "Sync BlendShape");
                    targetMesh.SetBlendShapeWeight(targetIndex, weight);
                }
            }
        }
    }
}
