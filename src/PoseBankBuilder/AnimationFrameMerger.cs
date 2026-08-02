// Co-coded with DeepSeek V4 Flash (max) in OpenCode

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
/// 输出格式对齐 BUDDYWORKS Poses Extension 官方 Posebank Creator：
/// - 帧率默认 60，第 i 个 Pose 写在 i / FPS 秒处；
/// - 帧间切线默认 Constant（离散硬切换）；可选 Linear（官方一致，对采样时间
///   误差更稳健）或 Auto；
/// - Clip 设置 keepOriginalPositionY/XZ = true 等（官方一致），使 Root 原点曲线
///   逐帧直接应用、不经过 root-motion 跨帧积分，每个单帧动作的原点只取决于
///   它自己的源动画；
/// - 源动画缺少 Root 原点曲线时继承列表内第一个可用值，避免原点被拉回地面；
/// - 原点矫正（默认开）：root 水平偏移超过阈值的动作（如奔跑/行走）移回原点，
///   保留各自高度。
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

    private enum PoseTangentMode
    {
        Linear,
        Constant,
        Auto
    }

    [SerializeField] private List<AnimationClip> sourceClips = new List<AnimationClip>();
    [SerializeField] private int outputFrameRate = 60;
    [SerializeField] private bool animatorCurvesOnly = true;
    [SerializeField] private bool includeObjectReferenceCurves = false;
    [SerializeField] private PoseTangentMode poseTangentMode = PoseTangentMode.Constant;
    [SerializeField] private bool originCorrection = true;
    [SerializeField] private float originCorrectionThreshold = 0.1f;
    [SerializeField] private bool loopPosebank = false;
    [SerializeField] private MissingCurveMode missingCurveMode = MissingCurveMode.UseZero;
    [SerializeField] private Vector2 scrollPosition;

    // ---- 页签与使用说明折叠状态（与 Vpd2Anim 一致） ----
    [SerializeField] private int tab;
    [SerializeField] private bool[] folds = new bool[6];
    private Vector2 helpScroll;

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

        tab = GUILayout.Toolbar(tab, new[] { "Pose Bank 转换", "使用说明" });
        EditorGUILayout.Space();

        if (tab == 1)
        {
            DrawHelp();
            return;
        }

        DrawSelectionButtons();
        DrawDropArea();
        DrawClipList();
        DrawSettings();
        DrawBuildButton();
    }

    // ------------------------------------------------------------------
    // 使用说明（折叠菜单，与 Vpd2Anim 一致）
    // ------------------------------------------------------------------
    private void DrawHelp()
    {
        helpScroll = EditorGUILayout.BeginScrollView(helpScroll);
        HelpFold(0, "快速上手",
            "1. 在 Project 窗口选中多个 AnimationClip，或直接拖入列表（顺序即帧顺序）\n" +
            "2. 调整输出设置（一般默认即可，详见下方各折叠项）\n" +
            "3. 点【生成 Pose Bank AnimationClip】，选择一个保存路径\n" +
            "4. 把生成的 .anim 放进 BUDDYWORKS Poses Extension 的\n" +
            "   Custom Poses 槽位（Action Controller → Custom / Custom (Mirror)）\n\n" +
            "每个源 AnimationClip 只读取本地时间 0 秒（即第 0 帧）的状态，\n" +
            "并按列表顺序写入输出 Clip 的第 0、1、2……帧。");
        HelpFold(1, "帧间切线 —— 每个 Pose 之间如何衔接",
            "● Constant（默认，离散硬切换）\n" +
            "  相邻帧之间不做插值，逐帧采样时每个 Pose 都是精确的离散值，最像\n" +
            "  独立的 Pose 快照。注意：动画器采样时间若比关键帧时间偏差一个\n" +
            "  微小量，整帧会落到前一帧的值（比如卧躺后第一帧站立显示成卧躺）。\n" +
            "  若出现这种情况，改用 Linear 即可。\n" +
            "● Linear（与 BUDDYWORKS 官方 Posebank Creator 一致）\n" +
            "  帧间线性过渡，对采样时间的微小误差不敏感，官方格式即此设置；\n" +
            "  缺点是逐帧扫描时会看到相邻 Pose 之间的过渡姿态。\n" +
            "● Auto（Unity 默认样条）\n" +
            "  帧间平滑曲线插值，适合做连续动画；离散 Pose 库一般不用。");
        HelpFold(2, "Root 原点 —— 每个单帧动作的原点为什么互不影响",
            "● 输出 Clip 使用与 BUDDYWORKS 官方一致的设置：\n" +
            "  keepOriginalPositionY/XZ、keepOriginalOrientation = true，\n" +
            "  Root 原点曲线逐帧直接应用，不经过 root-motion 的跨帧积分/平滑，\n" +
            "  因此每个 Pose 的原点只取决于它自己的源动画。\n" +
            "● 每个单帧动作只读取它自己源动画第 0 帧的 RootT（原点）值。\n" +
            "● 卧躺等不同高度的动作混用时，站立动作的原点不会被卧躺动作拉低；\n" +
            "  每个动作都保持自己的高度（如 Vpd2Anim 生成的 RootT.y 高度）。\n" +
            "● 源动画缺少 Root 原点曲线（RootT./RootQ. 或空路径 m_LocalPosition）\n" +
            "  时，不会写 0，而是继承列表内第一个含该曲线的 Pose 的值，\n" +
            "  避免该 Pose 的原点直接沉到地面。\n" +
            "● 原点矫正（默认开）\n" +
            "  源动画（如奔跑/行走动作）的 root 相对原点可能有较大的水平偏移\n" +
            "  （前后/左右）。生成时会把水平偏移超过阈值（默认 0.1 米，可在\n" +
            "  转换页调整）的动作移回原点：只归零水平分量（RootT.x/RootT.z，\n" +
            "  Unity 中即 X/Z 方向），保留高度（RootT.y，Unity 中即 Y 轴），\n" +
            "  因此站立/卧躺等姿势不受影响。");
        HelpFold(3, "源动画缺少某条曲线时（Use Zero / Abort）",
            "● Use Zero（默认）\n" +
            "  浮点曲线写 0、对象引用写 null。很适合 Humanoid Muscle：未记录的\n" +
            "  肌肉值通常应回到 0。但 Root 原点曲线（RootT./RootQ. 或空路径的\n" +
            "  m_LocalPosition）缺失时不会写 0，而是继承列表内第一个含该曲线的\n" +
            "  Pose 的值，避免该 Pose 的原点被拉回地面。\n" +
            "● Abort\n" +
            "  立即停止并报告缺少的曲线（路径/类型/属性）。\n" +
            "  若源动画是 Transform/Generic 动画，建议改用 Abort 检查曲线是否完整。");
        HelpFold(4, "循环 PoseBank / 输出帧率 / 曲线范围",
            "● 循环 PoseBank（默认关）\n" +
            "  在末尾重复第一个 Pose（官方 Loop Posebank 行为），便于无缝循环；\n" +
            "  离散 Pose 库通常不需要，且会使帧数 +1。\n" +
            "● 输出帧率（默认 60）\n" +
            "  第 i 个 Pose 写在 i / FPS 秒处，BUDDYWORKS 官方格式即 60fps。\n" +
            "● 仅合并 Animator 曲线（默认开）\n" +
            "  只保留类型为 Animator 的曲线（Humanoid/Muscle 属性），\n" +
            "  VRChat Pose Bank 推荐；Transform/Generic 曲线会被忽略。\n" +
            "● 包含对象引用曲线（默认关）\n" +
            "  例如材质、Sprite 等对象引用。普通 Humanoid Pose 通常不需要。");
        HelpFold(5, "注意事项",
            "● 输出 Clip 需作为 BUDDYWORKS Poses Extension 的自定义 Pose Bank\n" +
            "  使用（Custom Poses → Custom / Custom (Mirror) 槽位）。\n" +
            "● 源动画只读取第 0 帧（Vpd2Anim 生成的单帧 Clip 正合适）。\n" +
            "● 生成后请在 Unity 动画预览窗口中逐帧检查每个 Pose。");
        EditorGUILayout.EndScrollView();
    }

    private void HelpFold(int i, string title, string body)
    {
        folds[i] = EditorGUILayout.Foldout(folds[i], title, true, EditorStyles.foldoutHeader);
        if (!folds[i])
        {
            return;
        }

        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField(body, EditorStyles.wordWrappedLabel);
        EditorGUI.indentLevel--;
        EditorGUILayout.Space();
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

        poseTangentMode = (PoseTangentMode)EditorGUILayout.EnumPopup(
            new GUIContent(
                "帧间切线",
                "Constant（默认）：离散硬切换，逐帧采样最精确；若切换 Pose 时出现显示成前一帧的情况，可改用 Linear。Linear：与 BUDDYWORKS 官方 Posebank Creator 一致，对采样时间误差更稳健。Auto：Unity 默认样条。详见「使用说明」页。"),
            poseTangentMode);

        originCorrection = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "原点矫正（将偏移过大的动作移回原点）",
                "源动画的 root 相对原点水平偏移过大时（例如奔跑/行走动作），把该动作的水平偏移归零、移回原点，保留各自高度。"),
            originCorrection);

        if (originCorrection)
        {
            originCorrectionThreshold = EditorGUILayout.FloatField(
                new GUIContent("矫正阈值（米）", "水平偏移（前后/左右）超过该值即移回原点。"),
                originCorrectionThreshold);
            originCorrectionThreshold = Mathf.Max(0f, originCorrectionThreshold);
        }

        loopPosebank = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "循环 PoseBank（末尾重复第一个 Pose）",
                "与 BUDDYWORKS 官方 Posebank Creator 的 Loop Posebank 一致：在末尾追加第一个 Pose，便于做无缝循环。离散 Pose 库通常不需要。"),
            loopPosebank);

        missingCurveMode = (MissingCurveMode)EditorGUILayout.EnumPopup(
            new GUIContent(
                "源动画缺少某条曲线时",
                "Use Zero：浮点曲线写 0、对象引用写 null（Root 原点曲线除外，见下）；Abort：立即停止并报告。"),
            missingCurveMode);
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
                    "Pose 数：{0}\n浮点曲线：{1}\n对象引用曲线：{2}\n缺失曲线采样：{3}\nRoot 原点继承：{4}\n原点矫正：{5}\n输出：{6}",
                    result.PoseCount,
                    result.FloatCurveCount,
                    result.ObjectCurveCount,
                    result.MissingSampleCount,
                    result.RootInheritSampleCount,
                    result.OriginCorrectedPoseCount,
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

        // 与 BUDDYWORKS 官方 Posebank Creator 相同的 Clip 设置：
        // keepOriginal* = true 使 Root 原点曲线逐帧直接应用，不经过 root-motion 的
        // 跨帧积分/平滑管线，保证每个单帧动作的原点只取决于它自己，互不影响。
        AnimationClipSettings clipSettings = AnimationUtility.GetAnimationClipSettings(output);
        clipSettings.loopTime = false;
        clipSettings.mirror = false;
        clipSettings.loopBlendOrientation = true;
        clipSettings.keepOriginalOrientation = true;
        clipSettings.orientationOffsetY = 0f;
        clipSettings.loopBlendPositionY = true;
        clipSettings.keepOriginalPositionY = true;
        clipSettings.loopBlendPositionXZ = true;
        clipSettings.keepOriginalPositionXZ = true;
        clipSettings.level = 0f;
        AnimationUtility.SetAnimationClipSettings(output, clipSettings);

        List<AnimationClip> processClips = new List<AnimationClip>(clips);
        if (loopPosebank && processClips.Count > 0 && processClips[0] != null)
        {
            processClips.Add(processClips[0]);
        }

        int poseCount = processClips.Count;

        List<Dictionary<BindingKey, AnimationCurve>> sourceFloatCurves =
            new List<Dictionary<BindingKey, AnimationCurve>>(poseCount);
        List<Dictionary<BindingKey, ObjectReferenceKeyframe[]>> sourceObjectCurves =
            new List<Dictionary<BindingKey, ObjectReferenceKeyframe[]>>(poseCount);

        Dictionary<BindingKey, EditorCurveBinding> floatBindingUnion =
            new Dictionary<BindingKey, EditorCurveBinding>();
        Dictionary<BindingKey, EditorCurveBinding> objectBindingUnion =
            new Dictionary<BindingKey, EditorCurveBinding>();

        foreach (AnimationClip clip in processClips)
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
        int rootInheritSampleCount = 0;

        // ---- 原点矫正：水平偏移过大的动作移回原点（保留高度） ----
        bool[] originCorrectedPoses = null;
        int originCorrectedPoseCount = 0;
        if (originCorrection)
        {
            originCorrectedPoses = new bool[poseCount];
            float thresholdSq = originCorrectionThreshold * originCorrectionThreshold;
            for (int poseIndex = 0; poseIndex < poseCount; poseIndex++)
            {
                float horizontalSq = 0f;
                foreach (KeyValuePair<BindingKey, AnimationCurve> kvp in sourceFloatCurves[poseIndex])
                {
                    EditorCurveBinding binding;
                    if (!floatBindingUnion.TryGetValue(kvp.Key, out binding))
                    {
                        continue;
                    }

                    if (!IsHorizontalRootBinding(binding))
                    {
                        continue;
                    }

                    float v = EvaluateFirstFrame(kvp.Value);
                    horizontalSq += v * v;
                }

                if (horizontalSq > thresholdSq)
                {
                    originCorrectedPoses[poseIndex] = true;
                    originCorrectedPoseCount++;
                }
            }
        }

        foreach (EditorCurveBinding binding in orderedFloatBindings)
        {
            BindingKey key = new BindingKey(binding);
            Keyframe[] outputKeys = new Keyframe[poseCount];
            bool isRootBinding = IsRootOriginBinding(binding);
            float? inheritedRootValue = null;

            // 当该 Pose 缺少 Root 原点曲线时，先找列表内第一个含该曲线的 Pose，
            // 用它的值作为原点继承值（而不是写 0 把原点拉回地面）。
            if (isRootBinding && missingCurveMode == MissingCurveMode.UseZero)
            {
                for (int k = 0; k < poseCount; k++)
                {
                    AnimationCurve candidate;
                    if (sourceFloatCurves[k].TryGetValue(key, out candidate) && candidate != null)
                    {
                        inheritedRootValue = EvaluateFirstFrame(candidate);
                        break;
                    }
                }
            }

            for (int poseIndex = 0; poseIndex < poseCount; poseIndex++)
            {
                float value;
                AnimationCurve sourceCurve;

                if (sourceFloatCurves[poseIndex].TryGetValue(key, out sourceCurve))
                {
                    // 与官方实现一致：取源 Clip 首个关键帧的值（Vpd2Anim 生成的
                    // 单帧 Clip 关键帧就在时间 0，等价于 Evaluate(0)）。
                    value = EvaluateFirstFrame(sourceCurve);
                }
                else if (isRootBinding && inheritedRootValue.HasValue)
                {
                    rootInheritSampleCount++;
                    value = inheritedRootValue.Value;
                }
                else
                {
                    missingSampleCount++;
                    HandleMissingCurve(processClips[poseIndex], binding);
                    value = 0f;
                }

                // 原点矫正：该动作水平偏移过大时，水平 Root 分量归零。
                if (originCorrectedPoses != null
                    && originCorrectedPoses[poseIndex]
                    && IsHorizontalRootBinding(binding))
                {
                    value = 0f;
                }

                float outputTime = poseIndex / (float)outputFrameRate;
                outputKeys[poseIndex] = new Keyframe(outputTime, value);
            }

            AnimationCurve outputCurve = new AnimationCurve(outputKeys);
            ApplyTangentMode(outputCurve);

            AnimationUtility.SetEditorCurve(output, binding, outputCurve);
        }

        foreach (EditorCurveBinding binding in orderedObjectBindings)
        {
            BindingKey key = new BindingKey(binding);
            ObjectReferenceKeyframe[] outputKeys = new ObjectReferenceKeyframe[poseCount];

            for (int poseIndex = 0; poseIndex < poseCount; poseIndex++)
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
                    HandleMissingCurve(processClips[poseIndex], binding);
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
            poseCount,
            orderedFloatBindings.Count,
            orderedObjectBindings.Count,
            missingSampleCount,
            rootInheritSampleCount,
            originCorrectedPoseCount);
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

    private static float EvaluateFirstFrame(AnimationCurve curve)
    {
        if (curve == null || curve.length == 0)
        {
            return 0f;
        }

        return curve.keys[0].value;
    }

    private static bool IsRootOriginBinding(EditorCurveBinding binding)
    {
        if (binding.type == typeof(Animator))
        {
            return binding.propertyName.StartsWith("RootT.", StringComparison.Ordinal)
                   || binding.propertyName.StartsWith("RootQ.", StringComparison.Ordinal);
        }

        // Generic/Transform 动画：空路径上的 m_LocalPosition 即根骨骼（原点）。
        return string.IsNullOrEmpty(binding.path)
               && binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal);
    }

    /// <summary>
    /// 水平方向（非高度）的 Root 分量。本工具面向的 VRChat/Unity 空间中
    /// X = 左右、Z = 前后（水平），Y = 高度（上下）——肌肉空间与 Generic
    /// 层级空间均为这一约定。原点矫正只归零这些水平分量，保留高度 Y。
    /// </summary>
    private static bool IsHorizontalRootBinding(EditorCurveBinding binding)
    {
        if (binding.type == typeof(Animator))
        {
            return binding.propertyName == "RootT.x"
                   || binding.propertyName == "RootT.z";
        }

        return string.IsNullOrEmpty(binding.path)
               && (binding.propertyName == "m_LocalPosition.x"
                   || binding.propertyName == "m_LocalPosition.z");
    }

    private void ApplyTangentMode(AnimationCurve curve)
    {
        if (curve == null)
        {
            return;
        }

        for (int i = 0; i < curve.length; i++)
        {
            switch (poseTangentMode)
            {
                case PoseTangentMode.Linear:
                    AnimationUtility.SetKeyLeftTangentMode(
                        curve, i, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(
                        curve, i, AnimationUtility.TangentMode.Linear);
                    break;

                case PoseTangentMode.Constant:
                    AnimationUtility.SetKeyLeftTangentMode(
                        curve, i, AnimationUtility.TangentMode.Constant);
                    AnimationUtility.SetKeyRightTangentMode(
                        curve, i, AnimationUtility.TangentMode.Constant);
                    break;

                default:
                    AnimationUtility.SetKeyLeftTangentMode(
                        curve, i, AnimationUtility.TangentMode.Auto);
                    AnimationUtility.SetKeyRightTangentMode(
                        curve, i, AnimationUtility.TangentMode.Auto);
                    break;
            }
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
        public readonly int PoseCount;
        public readonly int FloatCurveCount;
        public readonly int ObjectCurveCount;
        public readonly int MissingSampleCount;
        public readonly int RootInheritSampleCount;
        public readonly int OriginCorrectedPoseCount;

        public BuildResult(
            AnimationClip clip,
            int poseCount,
            int floatCurveCount,
            int objectCurveCount,
            int missingSampleCount,
            int rootInheritSampleCount,
            int originCorrectedPoseCount)
        {
            Clip = clip;
            PoseCount = poseCount;
            FloatCurveCount = floatCurveCount;
            ObjectCurveCount = objectCurveCount;
            MissingSampleCount = missingSampleCount;
            RootInheritSampleCount = rootInheritSampleCount;
            OriginCorrectedPoseCount = originCorrectedPoseCount;
        }
    }
}
