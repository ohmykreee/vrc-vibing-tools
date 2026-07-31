// Co-coded with ChatGPT 5.5 Thinking (high, free account) in Webchat

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 将多个 AnimationClip 的第 0 帧合并为一个 Pose Bank：
/// 第 i 个源 Clip 的第 0 帧 -> 输出 Clip 的第 i 帧。
///
/// 请将本文件放在 Assets/Editor/ 目录下。
/// </summary>
public sealed class PoseBankBuilderWindow : EditorWindow
{
    private enum MissingCurveMode
    {
        UseZero,
        Abort
    }

    [SerializeField] private List<AnimationClip> sourceClips = new List<AnimationClip>();
    [SerializeField] private int outputFrameRate = 60;
    [SerializeField] private bool animatorCurvesOnly = true;
    [SerializeField] private bool includeObjectReferenceCurves = false;
    [SerializeField] private bool useConstantTangents = true;
    [SerializeField] private MissingCurveMode missingCurveMode = MissingCurveMode.UseZero;
    [SerializeField] private Vector2 scrollPosition;

    [MenuItem("Tools/Vibing Tools/Pose Bank Builder", priority = 30)]
    private static void OpenWindow()
    {
        PoseBankBuilderWindow window = GetWindow<PoseBankBuilderWindow>();
        window.titleContent = new GUIContent("Pose Bank Builder");
        window.minSize = new Vector2(520f, 420f);
        window.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("VRChat Pose Bank Builder", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "每个源 AnimationClip 只读取本地时间 0 秒的状态，并依照列表顺序写入输出 Clip 的第 0、1、2……帧。",
            MessageType.Info);

        DrawSelectionButtons();
        DrawDropArea();
        DrawClipList();
        DrawSettings();
        DrawBuildButton();
    }

    private void DrawSelectionButtons()
    {
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("从 Project 当前选中项载入"))
        {
            AnimationClip[] selected = Selection.objects
                .OfType<AnimationClip>()
                .Where(clip => clip != null)
                .ToArray();

            if (selected.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "没有 AnimationClip",
                    "请先在 Project 窗口中选中一个或多个 AnimationClip。",
                    "确定");
            }
            else
            {
                sourceClips = new List<AnimationClip>(selected);
                Repaint();
            }
        }

        if (GUILayout.Button("添加空项", GUILayout.Width(90f)))
        {
            sourceClips.Add(null);
        }

        if (GUILayout.Button("清空", GUILayout.Width(70f)))
        {
            sourceClips.Clear();
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawDropArea()
    {
        Rect dropRect = GUILayoutUtility.GetRect(0f, 48f, GUILayout.ExpandWidth(true));
        GUI.Box(dropRect, "也可以把 AnimationClip 拖到这里");

        Event evt = Event.current;
        if (!dropRect.Contains(evt.mousePosition))
        {
            return;
        }

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            bool containsClip = DragAndDrop.objectReferences.Any(obj => obj is AnimationClip);
            DragAndDrop.visualMode = containsClip ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

            if (containsClip && evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (Object obj in DragAndDrop.objectReferences)
                {
                    AnimationClip clip = obj as AnimationClip;
                    if (clip != null)
                    {
                        sourceClips.Add(clip);
                    }
                }
            }

            evt.Use();
        }
    }

    private void DrawClipList()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("源动画列表（列表顺序就是输出帧顺序）", EditorStyles.boldLabel);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.MinHeight(150f));

        for (int i = 0; i < sourceClips.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(i.ToString(), GUILayout.Width(28f));
            sourceClips[i] = (AnimationClip)EditorGUILayout.ObjectField(
                sourceClips[i], typeof(AnimationClip), false);

            EditorGUI.BeginDisabledGroup(i == 0);
            if (GUILayout.Button("▲", GUILayout.Width(28f)))
            {
                Swap(i, i - 1);
                GUIUtility.ExitGUI();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(i >= sourceClips.Count - 1);
            if (GUILayout.Button("▼", GUILayout.Width(28f)))
            {
                Swap(i, i + 1);
                GUIUtility.ExitGUI();
            }
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("×", GUILayout.Width(28f)))
            {
                sourceClips.RemoveAt(i);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();

        int validCount = sourceClips.Count(clip => clip != null);
        EditorGUILayout.LabelField("有效 Pose 数", validCount.ToString());
    }

    private void DrawSettings()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("输出设置", EditorStyles.boldLabel);

        outputFrameRate = EditorGUILayout.IntField(
            new GUIContent("输出帧率", "第 i 个 Pose 会写在 i / FPS 秒处。"),
            outputFrameRate);
        outputFrameRate = Mathf.Clamp(outputFrameRate, 1, 240);

        animatorCurvesOnly = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "仅合并 Animator 曲线（Humanoid Pose Bank 推荐）",
                "只保留类型为 Animator 的曲线，例如截图中的胸部、头部、手臂和下颌等 Humanoid/Muscle 属性。"),
            animatorCurvesOnly);

        includeObjectReferenceCurves = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "包含对象引用曲线",
                "例如材质、Sprite 等对象引用。普通 Humanoid Pose 通常不需要。"),
            includeObjectReferenceCurves);

        useConstantTangents = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "使用 Constant 切线",
                "使相邻 Pose 之间不做连续插值；逐帧采样时更像离散 Pose。"),
            useConstantTangents);

        missingCurveMode = (MissingCurveMode)EditorGUILayout.EnumPopup(
            new GUIContent(
                "源动画缺少某条曲线时",
                "Use Zero：浮点曲线写 0、对象引用写 null；Abort：立即停止并报告。"),
            missingCurveMode);

        if (missingCurveMode == MissingCurveMode.UseZero)
        {
            EditorGUILayout.HelpBox(
                "Use Zero 很适合 Humanoid Muscle：未记录的肌肉值通常应回到 0。若源动画是 Transform/Generic 动画，建议改用 Abort 检查曲线是否完整。",
                MessageType.None);
        }
    }

    private void DrawBuildButton()
    {
        EditorGUILayout.Space(8f);

        List<AnimationClip> validClips = sourceClips.Where(clip => clip != null).ToList();
        EditorGUI.BeginDisabledGroup(validClips.Count == 0);

        if (GUILayout.Button("生成 Pose Bank AnimationClip", GUILayout.Height(34f)))
        {
            BuildAndSave(validClips);
        }

        EditorGUI.EndDisabledGroup();
    }

    private void BuildAndSave(List<AnimationClip> validClips)
    {
        string path = EditorUtility.SaveFilePanelInProject(
            "保存 Pose Bank",
            "PoseBank",
            "anim",
            "请选择输出 AnimationClip 的保存位置。");

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Object existing = AssetDatabase.LoadAssetAtPath<Object>(path);
        if (existing != null)
        {
            bool replace = EditorUtility.DisplayDialog(
                "覆盖已有资源？",
                "该路径已经存在资源：\n" + path + "\n\n是否删除并重新生成？",
                "覆盖",
                "取消");

            if (!replace)
            {
                return;
            }

            if (!AssetDatabase.DeleteAsset(path))
            {
                EditorUtility.DisplayDialog("删除失败", "无法删除已有资源：\n" + path, "确定");
                return;
            }
        }

        try
        {
            BuildResult result = BuildPoseBank(validClips);
            result.Clip.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(result.Clip, path);
            EditorUtility.SetDirty(result.Clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = result.Clip;
            EditorGUIUtility.PingObject(result.Clip);

            EditorUtility.DisplayDialog(
                "Pose Bank 已生成",
                string.Format(
                    "Pose 数：{0}\n浮点曲线：{1}\n对象引用曲线：{2}\n缺失曲线采样：{3}\n输出：{4}",
                    validClips.Count,
                    result.FloatCurveCount,
                    result.ObjectCurveCount,
                    result.MissingSampleCount,
                    path),
                "确定");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog(
                "生成失败",
                exception.Message + "\n\n详细堆栈已输出到 Console。",
                "确定");
        }
    }

    private BuildResult BuildPoseBank(List<AnimationClip> clips)
    {
        if (clips == null || clips.Count == 0)
        {
            throw new InvalidOperationException("没有可用的源 AnimationClip。");
        }

        AnimationClip output = new AnimationClip
        {
            name = "PoseBank",
            frameRate = outputFrameRate,
            legacy = false,
            wrapMode = WrapMode.ClampForever
        };

        List<Dictionary<BindingKey, AnimationCurve>> sourceFloatCurves =
            new List<Dictionary<BindingKey, AnimationCurve>>(clips.Count);
        List<Dictionary<BindingKey, ObjectReferenceKeyframe[]>> sourceObjectCurves =
            new List<Dictionary<BindingKey, ObjectReferenceKeyframe[]>>(clips.Count);

        Dictionary<BindingKey, EditorCurveBinding> floatBindingUnion =
            new Dictionary<BindingKey, EditorCurveBinding>();
        Dictionary<BindingKey, EditorCurveBinding> objectBindingUnion =
            new Dictionary<BindingKey, EditorCurveBinding>();

        foreach (AnimationClip clip in clips)
        {
            Dictionary<BindingKey, AnimationCurve> floatMap =
                new Dictionary<BindingKey, AnimationCurve>();

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!ShouldIncludeBinding(binding))
                {
                    continue;
                }

                BindingKey key = new BindingKey(binding);
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null)
                {
                    floatMap[key] = curve;
                    if (!floatBindingUnion.ContainsKey(key))
                    {
                        floatBindingUnion.Add(key, binding);
                    }
                }
            }

            sourceFloatCurves.Add(floatMap);

            Dictionary<BindingKey, ObjectReferenceKeyframe[]> objectMap =
                new Dictionary<BindingKey, ObjectReferenceKeyframe[]>();

            if (includeObjectReferenceCurves)
            {
                foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!ShouldIncludeBinding(binding))
                    {
                        continue;
                    }

                    BindingKey key = new BindingKey(binding);
                    ObjectReferenceKeyframe[] keys =
                        AnimationUtility.GetObjectReferenceCurve(clip, binding);

                    if (keys != null && keys.Length > 0)
                    {
                        objectMap[key] = keys;
                        if (!objectBindingUnion.ContainsKey(key))
                        {
                            objectBindingUnion.Add(key, binding);
                        }
                    }
                }
            }

            sourceObjectCurves.Add(objectMap);
        }

        if (floatBindingUnion.Count == 0 && objectBindingUnion.Count == 0)
        {
            throw new InvalidOperationException(
                animatorCurvesOnly
                    ? "没有找到 Animator 曲线。请确认源动画包含 Humanoid/Animator 数据，或取消“仅合并 Animator 曲线”。"
                    : "源动画中没有找到可合并的动画曲线。");
        }

        List<EditorCurveBinding> orderedFloatBindings = floatBindingUnion.Values.ToList();
        orderedFloatBindings.Sort(CompareBindings);

        List<EditorCurveBinding> orderedObjectBindings = objectBindingUnion.Values.ToList();
        orderedObjectBindings.Sort(CompareBindings);

        int missingSampleCount = 0;

        foreach (EditorCurveBinding binding in orderedFloatBindings)
        {
            BindingKey key = new BindingKey(binding);
            Keyframe[] outputKeys = new Keyframe[clips.Count];

            for (int poseIndex = 0; poseIndex < clips.Count; poseIndex++)
            {
                float value;
                AnimationCurve sourceCurve;

                if (sourceFloatCurves[poseIndex].TryGetValue(key, out sourceCurve))
                {
                    // AnimationCurve.Evaluate(0) 即源 Clip 本地时间 0 秒，也就是第 0 帧。
                    value = sourceCurve.Evaluate(0f);
                }
                else
                {
                    missingSampleCount++;
                    HandleMissingCurve(clips[poseIndex], binding);
                    value = 0f;
                }

                float outputTime = poseIndex / (float)outputFrameRate;
                outputKeys[poseIndex] = new Keyframe(outputTime, value);
            }

            AnimationCurve outputCurve = new AnimationCurve(outputKeys);
            if (useConstantTangents)
            {
                SetConstantTangents(outputCurve);
            }

            AnimationUtility.SetEditorCurve(output, binding, outputCurve);
        }

        foreach (EditorCurveBinding binding in orderedObjectBindings)
        {
            BindingKey key = new BindingKey(binding);
            ObjectReferenceKeyframe[] outputKeys = new ObjectReferenceKeyframe[clips.Count];

            for (int poseIndex = 0; poseIndex < clips.Count; poseIndex++)
            {
                Object value;
                ObjectReferenceKeyframe[] sourceKeys;

                if (sourceObjectCurves[poseIndex].TryGetValue(key, out sourceKeys))
                {
                    value = EvaluateObjectReferenceAtZero(sourceKeys);
                }
                else
                {
                    missingSampleCount++;
                    HandleMissingCurve(clips[poseIndex], binding);
                    value = null;
                }

                outputKeys[poseIndex] = new ObjectReferenceKeyframe
                {
                    time = poseIndex / (float)outputFrameRate,
                    value = value
                };
            }

            AnimationUtility.SetObjectReferenceCurve(output, binding, outputKeys);
        }

        return new BuildResult(
            output,
            orderedFloatBindings.Count,
            orderedObjectBindings.Count,
            missingSampleCount);
    }

    private bool ShouldIncludeBinding(EditorCurveBinding binding)
    {
        return !animatorCurvesOnly || binding.type == typeof(Animator);
    }

    private void HandleMissingCurve(AnimationClip sourceClip, EditorCurveBinding binding)
    {
        if (missingCurveMode != MissingCurveMode.Abort)
        {
            return;
        }

        throw new InvalidOperationException(
            string.Format(
                "源动画“{0}”缺少曲线：\nPath: {1}\nType: {2}\nProperty: {3}",
                sourceClip != null ? sourceClip.name : "<null>",
                string.IsNullOrEmpty(binding.path) ? "<root>" : binding.path,
                binding.type != null ? binding.type.FullName : "<null>",
                binding.propertyName));
    }

    private static Object EvaluateObjectReferenceAtZero(ObjectReferenceKeyframe[] keys)
    {
        if (keys == null || keys.Length == 0)
        {
            return null;
        }

        Object value = keys[0].value;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].time <= 0.000001f)
            {
                value = keys[i].value;
            }
            else
            {
                break;
            }
        }

        return value;
    }

    private static void SetConstantTangents(AnimationCurve curve)
    {
        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(
                curve, i, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetKeyRightTangentMode(
                curve, i, AnimationUtility.TangentMode.Constant);
        }
    }

    private static int CompareBindings(EditorCurveBinding a, EditorCurveBinding b)
    {
        int pathCompare = string.Compare(a.path, b.path, StringComparison.Ordinal);
        if (pathCompare != 0)
        {
            return pathCompare;
        }

        string aType = a.type != null ? a.type.FullName : string.Empty;
        string bType = b.type != null ? b.type.FullName : string.Empty;
        int typeCompare = string.Compare(aType, bType, StringComparison.Ordinal);
        if (typeCompare != 0)
        {
            return typeCompare;
        }

        return string.Compare(a.propertyName, b.propertyName, StringComparison.Ordinal);
    }

    private void Swap(int a, int b)
    {
        AnimationClip temp = sourceClips[a];
        sourceClips[a] = sourceClips[b];
        sourceClips[b] = temp;
    }

    private struct BindingKey : IEquatable<BindingKey>
    {
        private readonly string path;
        private readonly Type type;
        private readonly string propertyName;

        public BindingKey(EditorCurveBinding binding)
        {
            path = binding.path ?? string.Empty;
            type = binding.type;
            propertyName = binding.propertyName ?? string.Empty;
        }

        public bool Equals(BindingKey other)
        {
            return string.Equals(path, other.path, StringComparison.Ordinal)
                   && type == other.type
                   && string.Equals(propertyName, other.propertyName, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is BindingKey && Equals((BindingKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + path.GetHashCode();
                hash = hash * 31 + (type != null ? type.GetHashCode() : 0);
                hash = hash * 31 + propertyName.GetHashCode();
                return hash;
            }
        }
    }

    private sealed class BuildResult
    {
        public readonly AnimationClip Clip;
        public readonly int FloatCurveCount;
        public readonly int ObjectCurveCount;
        public readonly int MissingSampleCount;

        public BuildResult(
            AnimationClip clip,
            int floatCurveCount,
            int objectCurveCount,
            int missingSampleCount)
        {
            Clip = clip;
            FloatCurveCount = floatCurveCount;
            ObjectCurveCount = objectCurveCount;
            MissingSampleCount = missingSampleCount;
        }
    }
}
