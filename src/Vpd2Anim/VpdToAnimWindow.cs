// Co-coded with Kimi K3 (max) & DeepSeek V4 Flash (0731 max) in OpenCode

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VpdToAnim
{
    /// <summary>
    /// VPD（MMD 姿势）→ Unity AnimationClip 转换器，面向 VRChat humanoid avatar。
    /// 菜单：Tools → VPD 姿势转换 (VRChat Humanoid)
    ///
    /// 「单个转换」：拖入一个 .vpd，可在场景中预览，生成一个 .anim。
    /// 「批量转换」：拖入多个 .vpd（或整个文件夹），全部转换到指定输出文件夹，文件名与原文件相同。
    /// 每个生成的 .anim 恰好只有 1 帧：即该静态姿势。
    /// </summary>
    public class VpdToAnimWindow : EditorWindow
    {
        [MenuItem("Tools/Vibing Tools/VPD → Anim Clips (VRChat Humanoid)", priority = 20)]
        static void Open() => GetWindow<VpdToAnimWindow>("VPD 姿势转换");

        enum OutputMode { HumanoidMuscle = 0, GenericTransform = 1 }

        // ---- 单个 ----
        [SerializeField] TextAsset _vpdAsset;
        [SerializeField] GameObject _avatar;
        [SerializeField] string _clipName = "";
        [SerializeField] string _outputFolder = "Assets/VPD Poses";

        // ---- 批量 ----
        [Serializable]
        class BatchItem
        {
            public TextAsset Asset;          // 工程内资产（.vpd 以 TextAsset 导入）
            public string ExternalPath;      // 工程外的系统文件
            public string Status = "";
            public string Label => Asset != null ? AssetDatabase.GetAssetPath(Asset) : ExternalPath;
            public string Name => Asset != null ? Asset.name : Path.GetFileNameWithoutExtension(ExternalPath);
        }
        [SerializeField] List<BatchItem> _batch = new List<BatchItem>();

        // ---- 共享选项 ----
        [SerializeField] bool _mirror;
        [SerializeField] bool _fingers = true;
        [SerializeField] bool _eyes;
        [SerializeField] bool _extraBones;
        [SerializeField] LegCorrectionMode _legCorrection = LegCorrectionMode.Auto;
        [SerializeField] AlignMode _align = AlignMode.Arms;
        [SerializeField] OutputMode _mode = OutputMode.HumanoidMuscle;
        [SerializeField] float _manualScale;

        // ---- 使用说明页的折叠状态 ----
        [SerializeField] bool[] _folds = new bool[7];

        VpdPose _pose;
        string _sourceLabel = "";
        string _log = "「单个转换」：拖入一个 .vpd 进行预览与转换。\n「批量转换」：拖入多个 .vpd 或整个文件夹批量转换。\n「使用说明」：各选项的作用与使用方法。";
        Vector2 _scroll, _batchScroll, _helpScroll;
        int _tab;
        AvatarRig _previewRig;

        static readonly GUIContent[] ModeLabels =
        {
            new GUIContent("Humanoid muscle（默认，VRChat 必选）"),
            new GUIContent("Generic transform（仅调试或特殊用途）"),
        };
        static readonly GUIContent[] AlignLabels =
        {
            new GUIContent("无（avatar 绑定姿势本身是 A-pose 时选）"),
            new GUIContent("仅手臂（默认，推荐）"),
            new GUIContent("全部（手臂+腿+脊柱）"),
        };
        static readonly GUIContent[] LegCorrectionLabels =
        {
            new GUIContent("无（原始 VPD，大幅姿势时脚踝与脚背可能翻转）"),
            new GUIContent("自动（默认，推荐）"),
        };

        // ==================================================================
        void OnGUI()
        {
            EditorGUILayout.Space();
            _tab = GUILayout.Toolbar(_tab, new[] { "单个转换", "批量转换", "使用说明" });
            EditorGUILayout.Space();

            if (_tab == 2) { DrawHelp(); return; }

            if (_tab == 0) DrawSingleSource();
            else DrawBatchList();

            EditorGUILayout.Space();
            DrawAvatarSection();
            EditorGUILayout.Space();
            DrawOptionsSection();
            EditorGUILayout.Space();
            DrawOutputSection();
            EditorGUILayout.Space();
            if (_tab == 0) DrawSingleActions();
            else DrawBatchActions();
            EditorGUILayout.Space();
            DrawLog();
        }

        // ------------------------------------------------------------------
        // 单个转换
        // ------------------------------------------------------------------
        void DrawSingleSource()
        {
            EditorGUILayout.LabelField("1. VPD 姿势文件", EditorStyles.boldLabel);
            var rect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            GUI.Box(rect, string.IsNullOrEmpty(_sourceLabel) ? "把一个 .vpd 文件拖到这里" : _sourceLabel, EditorStyles.helpBox);
            if (HandleDrag(rect, out var ta, out var path))
            {
                if (ta != null) LoadFromAsset(ta); else LoadFromBytes(File.ReadAllBytes(path), path);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                var newAsset = (TextAsset)EditorGUILayout.ObjectField(_vpdAsset, typeof(TextAsset), false);
                if (newAsset != _vpdAsset && newAsset != null) LoadFromAsset(newAsset);
                if (GUILayout.Button("浏览…", GUILayout.Width(70))) Browse();
            }
            if (_pose != null)
                EditorGUILayout.LabelField($"已解析：{_pose.Bones.Count} 根骨骼（{_pose.PosedCount} 根有姿势）— {_pose.ModelName}", EditorStyles.miniLabel);
            _clipName = EditorGUILayout.TextField("Clip 名称", _clipName);
        }

        void DrawSingleActions()
        {
            EditorGUILayout.LabelField("5. 转换", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_pose == null || _avatar == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("在场景中预览姿势")) Preview();
                    if (GUILayout.Button("重置姿势", GUILayout.Width(90))) ResetPreview();
                }
                if (GUILayout.Button("生成 .anim（1 帧姿势）", GUILayout.Height(26))) GenerateSingle();
            }
        }

        // ------------------------------------------------------------------
        // 批量转换
        // ------------------------------------------------------------------
        void DrawBatchList()
        {
            EditorGUILayout.LabelField("1. VPD 文件（批量）", EditorStyles.boldLabel);
            var rect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            GUI.Box(rect, $"把多个 .vpd 文件或文件夹拖到这里（已加入 {_batch.Count} 个）", EditorStyles.helpBox);
            if (HandleDragMulti(rect)) Repaint();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("添加文件…")) BrowseMulti();
                if (GUILayout.Button("添加文件夹…")) BrowseFolder();
                if (GUILayout.Button("清空", GUILayout.Width(60))) { _batch.Clear(); }
            }

            _batchScroll = EditorGUILayout.BeginScrollView(_batchScroll, GUILayout.MinHeight(60), GUILayout.MaxHeight(180));
            for (int i = _batch.Count - 1; i >= 0; i--)
            {
                var item = _batch[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.Name, GUILayout.MinWidth(120));
                    EditorGUILayout.LabelField(item.Status, EditorStyles.miniLabel);
                    if (GUILayout.Button("×", GUILayout.Width(22))) _batch.RemoveAt(i);
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("所有 .anim 都会写入下方输出文件夹，文件名与原 .vpd 相同。", EditorStyles.miniLabel);
        }

        void DrawBatchActions()
        {
            EditorGUILayout.LabelField("5. 转换", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_batch.Count == 0 || _avatar == null))
            {
                if (GUILayout.Button($"转换 {_batch.Count} 个文件 → .anim", GUILayout.Height(26))) GenerateBatch();
            }
        }

        // ------------------------------------------------------------------
        // 共享区域
        // ------------------------------------------------------------------
        void DrawAvatarSection()
        {
            EditorGUILayout.LabelField("2. VRChat Avatar（Humanoid）", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var newAvatar = (GameObject)EditorGUILayout.ObjectField(_avatar, typeof(GameObject), true);
                if (newAvatar != _avatar) { _avatar = newAvatar; _previewRig = null; if (_tab == 0) RefreshReport(); }
                using (new EditorGUI.DisabledScope(_avatar == null || PrefabUtility.IsPartOfPrefabAsset(_avatar)))
                    if (GUILayout.Button("恢复初始姿势", GUILayout.Width(95))) RestoreAvatarPose();
            }
            EditorGUILayout.LabelField("prefab 资产（强烈推荐）或场景对象。转换结束后场景 avatar 一定会自动恢复原姿势。", EditorStyles.miniLabel);
        }

        void DrawOptionsSection()
        {
            EditorGUILayout.LabelField("3. 转换选项", EditorStyles.boldLabel);
            _mode = (OutputMode)EditorGUILayout.Popup(new GUIContent("输出模式",
                "Humanoid muscle：muscle 曲线，可跨任意 humanoid avatar 重定向（VRChat 必选）。\n" +
                "Generic transform：骨骼 transform 曲线，只对当前 avatar 有效。"),
                (int)_mode, ModeLabels);
            _align = (AlignMode)EditorGUILayout.Popup(new GUIContent("静止姿势对齐",
                "MMD rest = A-pose，VRChat avatar 多为 T-pose。对齐手臂链可让姿势正确还原。"),
                (int)_align, AlignLabels);
            _mirror = EditorGUILayout.Toggle(new GUIContent("镜像（左右互换）", "把整个姿势左右翻转。"), _mirror);
            _fingers = EditorGUILayout.Toggle("手指", _fingers);
            _eyes = EditorGUILayout.Toggle("眼睛", _eyes);
            _extraBones = EditorGUILayout.Toggle(new GUIContent("额外骨骼（按名字匹配）",
                "为 avatar 中与 VPD 骨骼完全同名的非 humanoid 骨骼（MMD 系模型的头发/裙子等）写 transform 曲线。"), _extraBones);
            _legCorrection = (LegCorrectionMode)EditorGUILayout.Popup(new GUIContent("腿部扭转矫正",
                "少数 VPD 的足首 FK 扭转会超出该 avatar 的 muscle 范围，播放时脚踝/脚背会内外翻转。\n" +
                "「自动」（默认）会把多余扭转分配到小腿（外观不变），避免翻转。"),
                (int)_legCorrection, LegCorrectionLabels);
            _manualScale = EditorGUILayout.FloatField(new GUIContent("Hip 缩放（0 = 自动）",
                "髋部位移换算比例（米 / MMD 单位）。0 = 按 avatar 髋高 ÷ MMD 髋高自动计算。"), _manualScale);
        }

        void DrawOutputSection()
        {
            EditorGUILayout.LabelField("4. 输出文件夹", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputFolder = EditorGUILayout.TextField(_outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(26))) BrowseOutputFolder();
            }
        }

        void DrawLog()
        {
            EditorGUILayout.LabelField("转换报告", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(80));
            EditorGUILayout.TextArea(_log, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------
        // 使用说明（折叠菜单）
        // ------------------------------------------------------------------
        void DrawHelp()
        {
            _helpScroll = EditorGUILayout.BeginScrollView(_helpScroll);
            HelpFold(0, "快速上手",
                "【单个转换】\n" +
                "1. 把一个 .vpd 拖入拖放区（系统文件夹 / Project 窗口均可）\n" +
                "2. 指定 VRChat avatar（Rig 必须是 Humanoid）\n" +
                "3. 按需调整选项（一般默认即可），可先在场景中预览\n" +
                "4. 点【生成 .anim】，得到只有 1 帧的静态姿势动画\n\n" +
                "【批量转换】\n" +
                "1. 把多个 .vpd 或整个文件夹拖入列表\n" +
                "2. 指定 avatar 与输出文件夹\n" +
                "3. 点【转换】，每个 .vpd 生成同名 .anim");
            HelpFold(1, "输出模式 —— 应该选哪个？",
                "● Humanoid muscle（默认，VRChat 必选）\n" +
                "  经 HumanPoseHandler 提取 muscle 曲线。VRChat 会忽略 humanoid\n" +
                "  骨骼上的 transform 曲线，所以 VRChat 必须用这个；还能在任意\n" +
                "  humanoid avatar 之间重定向。\n" +
                "● Generic transform\n" +
                "  直接写该 avatar 骨骼的 transform 曲线，只对这一个模型有效，\n" +
                "  VRChat 上无效。仅调试/特殊用途。");
            HelpFold(2, "静止姿势对齐（Rest-pose alignment）",
                "MMD 的 rest 是 A-pose，VRChat avatar 多为 T-pose，直接套增量会让手臂偏高。\n" +
                "● 仅手臂（默认，推荐）：只把手臂链对齐到 MMD 的 A-pose 方向，最自然。\n" +
                "● 无：不做对齐。仅当 avatar 绑定 rest 本身就是 A-pose 时选。\n" +
                "● 全部：手臂+腿+脊柱全对齐。个别模型可能更准也可能不自然，以预览为准。");
            HelpFold(3, "镜像 / 手指 / 眼睛 / 额外骨骼 / 腿部扭转矫正",
                "● 镜像（左右互换）：整个姿势左右翻转（左手↔右手、左腿↔右腿）。\n" +
                "● 手指（默认开）：转换手指弯曲，建议保持开启。\n" +
                "● 眼睛（默认关）：转换眼球方向。VRChat 眼球一般由 SDK 眼动控制，建议关。\n" +
                "● 额外骨骼（默认关）：avatar 中与 VPD 骨骼完全同名的非 humanoid 骨骼\n" +
                "  （MMD 系模型的头发/裙子）也写曲线。一般保持关闭。\n" +
                "● 腿部扭转矫正（默认自动）：少数 VPD 的足首 FK 扭转会超出该 avatar 的 muscle 范围，\n" +
                "  播放时脚踝/脚背会内外翻转。「自动」会把多余扭转自动分配到小腿（外观不变）；\n" +
                "  选「无」则保持原始 VPD。");
            HelpFold(4, "Hip 缩放（0 = 自动）",
                "髋部位移的换算比例（米 / MMD 单位）。\n" +
                "0（默认）= 自动：按「avatar 髋部高度 ÷ MMD 髋部高度」计算，绝大多数情况正确。\n" +
                "只有自动结果明显不对（比例很特殊的模型）时才手动填，参考值约 0.08 ~ 0.12。");
            HelpFold(5, "腿部 IK（自动处理，无需设置）",
                "很多 VPD 里腿部 FK 骨骼（足/ひざ/足首）是单位旋转，腿部姿势由\n" +
                "「足ＩＫ」骨骼驱动。本工具检测到被摆动的足ＩＫ 时，会自动对该腿做\n" +
                "两腿 IK 解算。与 MMD 一致（参考 blender_mmd_tools 的行为）：足ＩＫ 的\n" +
                "位移驱动大腿/小腿的位置，足ＩＫ 的旋转被忽略；脚踝朝向始终来自\n" +
                "VPD 里足首的 FK 旋转。转换报告中会标注哪条腿走了 IK。");
            HelpFold(6, "恢复初始姿势 / 注意事项",
                "● 转换结束后场景 avatar 一定会自动恢复原姿势，不会残留。\n" +
                "● 场景 avatar 若已被摆出姿势：拖入 avatar 栏后点右侧【恢复初始姿势】，\n" +
                "  会从 prefab 资产把出厂（T-）姿势拷贝回来，可用 Ctrl+Z 撤销。\n" +
                "● 注意事项：\n" +
                "  · avatar 的导入设置必须是 Rig → Animation Type → Humanoid\n" +
                "  · 用场景对象预览/转换前，请让它处于 rest/T-pose\n" +
                "  · 生成的 .anim 只有 1 帧（静态姿势），直接放进 Animator/手势层使用\n" +
                "  · .vpd 拖进 Project 会以 TextAsset 导入（Shift-JIS 自动解码）");
            EditorGUILayout.EndScrollView();
        }

        void HelpFold(int i, string title, string body)
        {
            _folds[i] = EditorGUILayout.Foldout(_folds[i], title, true, EditorStyles.foldoutHeader);
            if (!_folds[i]) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(body, EditorStyles.wordWrappedLabel);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
        }

        // ------------------------------------------------------------------
        // 拖放辅助
        // ------------------------------------------------------------------
        static bool IsVpdPath(string p) =>
            !string.IsNullOrEmpty(p) && (p.EndsWith(".vpd", StringComparison.OrdinalIgnoreCase) ||
                                         p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

        static bool HandleDrag(Rect rect, out TextAsset asset, out string path)
        {
            asset = null; path = null;
            var e = Event.current;
            if (e == null || !rect.Contains(e.mousePosition)) return false;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return false;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type != EventType.DragPerform) { e.Use(); return false; }
            DragAndDrop.AcceptDrag();
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is TextAsset t) { asset = t; e.Use(); return true; }
            foreach (var p in DragAndDrop.paths)
                if (File.Exists(p) && IsVpdPath(p)) { path = p; e.Use(); return true; }
            e.Use();
            return false;
        }

        bool HandleDragMulti(Rect rect)
        {
            var e = Event.current;
            if (e == null || !rect.Contains(e.mousePosition)) return false;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return false;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type != EventType.DragPerform) { e.Use(); return false; }
            DragAndDrop.AcceptDrag();
            // 从 Project 窗口拖入时，同一文件会同时出现在 objectReferences 和 paths 中。
            // 先按资产加入并记录其路径，paths 循环里跳过这些路径，否则每个文件会进列表两次。
            var addedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is TextAsset t)
                {
                    AddBatch(t, null);
                    var ap = AssetDatabase.GetAssetPath(t);
                    if (!string.IsNullOrEmpty(ap)) addedAssetPaths.Add(Path.GetFullPath(ap));
                }
            foreach (var p in DragAndDrop.paths)
            {
                if (File.Exists(p) && IsVpdPath(p))
                {
                    if (!addedAssetPaths.Contains(Path.GetFullPath(p))) AddBatch(null, p);
                }
                else if (Directory.Exists(p)) AddFolderContents(p);
            }
            e.Use();
            return true;
        }

        void AddFolderContents(string folder)
        {
            foreach (var f in Directory.GetFiles(folder, "*.vpd", SearchOption.AllDirectories)) AddBatch(null, f);
        }

        void AddBatch(TextAsset asset, string path)
        {
            foreach (var b in _batch)
                if ((asset != null && b.Asset == asset) || (path != null && b.ExternalPath == path)) return;
            _batch.Add(new BatchItem { Asset = asset, ExternalPath = path });
        }

        void Browse()
        {
            var p = EditorUtility.OpenFilePanel("选择 VPD 姿势文件", "", "vpd,txt");
            if (!string.IsNullOrEmpty(p)) LoadFromBytes(File.ReadAllBytes(p), p);
        }

        void BrowseMulti()
        {
            var p = EditorUtility.OpenFilePanelWithFilters("选择 VPD 姿势文件", "",
                new[] { "VPD 姿势", "vpd,txt", "所有文件", "*" });
            if (!string.IsNullOrEmpty(p)) AddBatch(null, p);
        }

        void BrowseFolder()
        {
            var p = EditorUtility.OpenFolderPanel("选择包含 .vpd 文件的文件夹", "", "");
            if (!string.IsNullOrEmpty(p)) AddFolderContents(p);
        }

        void BrowseOutputFolder()
        {
            var p = EditorUtility.OpenFolderPanel("选择输出文件夹（必须在 Assets 内）", Application.dataPath, "");
            if (string.IsNullOrEmpty(p)) return;
            var rel = FileUtil.GetProjectRelativePath(p);
            if (rel.StartsWith("Assets", StringComparison.Ordinal)) _outputFolder = rel;
            else EditorUtility.DisplayDialog("VPD 姿势转换", "输出文件夹必须位于工程的 Assets 文件夹内。", "好");
        }

        // ------------------------------------------------------------------
        // 加载 / 解析
        // ------------------------------------------------------------------
        void LoadFromAsset(TextAsset ta)
        {
            // ScriptedImporter 已完成 Shift-JIS 解码，直接用 .text
            _vpdAsset = ta;
            LoadFromText(ta.text, AssetDatabase.GetAssetPath(ta));
        }

        void LoadFromBytes(byte[] bytes, string label) => LoadFromText(VpdPose.Decode(bytes), label);

        void LoadFromText(string text, string label)
        {
            _previewRig = null;
            try
            {
                _pose = VpdPose.Parse(text);
                _sourceLabel = label;
                if (string.IsNullOrEmpty(_clipName)) _clipName = Path.GetFileNameWithoutExtension(label);
                RefreshReport();
            }
            catch (Exception ex) { _pose = null; _sourceLabel = ""; _log = "解析 VPD 失败：\n" + ex.Message; }
            Repaint();
        }

        VpdRetargeter BuildRetargeter(AvatarRig rig, VpdPose pose)
        {
            var rt = new VpdRetargeter
            {
                Pose = pose, Mirror = _mirror, Align = _align, Fingers = _fingers, Eyes = _eyes,
                LegCorrection = _legCorrection, ManualScale = _manualScale
            };
            rt.Prepare(rig);
            return rt;
        }

        void RefreshReport()
        {
            if (_pose == null) return;
            if (_avatar == null)
            {
                _log = $"已加载：{_sourceLabel}\n骨骼：{_pose.Bones.Count}，有姿势：{_pose.PosedCount}\n指定 avatar 后可查看骨骼映射报告。";
                return;
            }
            using (var rig = AvatarRig.Create(_avatar, false, out var err))
            {
                if (rig == null) { _log = err; return; }
                _log = $"已加载：{_sourceLabel}\n" + BuildRetargeter(rig, _pose).BuildReport(rig);
            }
        }

        // ------------------------------------------------------------------
        // 预览（单个转换页，场景对象）
        // ------------------------------------------------------------------
        void Preview()
        {
            if (PrefabUtility.IsPartOfPrefabAsset(_avatar))
            { _log = "预览只对场景中的对象有效。请先把 avatar 拖进场景（也可以直接从 prefab 资产生成）。"; return; }
            if (_previewRig == null)
            {
                _previewRig = AvatarRig.Create(_avatar, false, out var err);
                if (_previewRig == null) { _log = err; return; }
            }
            Undo.RecordObjects(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Cast<UnityEngine.Object>(_previewRig.All)), "VPD 姿势预览");
            var rt = BuildRetargeter(_previewRig, _pose);
            rt.Apply(_previewRig);
            _log = "已应用预览姿势（Ctrl+Z 或【重置姿势】撤销）。\n" + rt.BuildReport(_previewRig);
            SceneView.RepaintAll();
        }

        void ResetPreview()
        {
            if (_previewRig == null) return;
            Undo.RecordObjects(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Cast<UnityEngine.Object>(_previewRig.All)), "VPD 姿势重置");
            _previewRig.RestoreRest();
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------
        // 恢复场景 avatar 到 prefab 出厂姿势（T-pose）
        // ------------------------------------------------------------------
        void RestoreAvatarPose()
        {
            if (_avatar == null) return;
            if (PrefabUtility.IsPartOfPrefabAsset(_avatar))
            { _log = "prefab 资产本身从不会被本工具修改。「恢复初始姿势」只对场景对象有效。"; return; }

            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(_avatar);
            if (string.IsNullOrEmpty(prefabPath))
            {
                _log = "该场景对象不是 prefab 实例，无法得知它的初始姿势。\n" +
                       "恢复方法：删掉它，重新把 avatar prefab 拖进场景（或不保存直接重开场景）。";
                return;
            }

            var assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (assetRoot == null) { _log = "加载 prefab 资产失败：" + prefabPath; return; }

            var temp = (GameObject)PrefabUtility.InstantiatePrefab(assetRoot);
            try
            {
                temp.hideFlags = HideFlags.HideAndDontSave;

                // 在临时副本中找到与 _avatar 对应的节点
                var instRoot = PrefabUtility.GetNearestPrefabInstanceRoot(_avatar);
                var segments = new List<string>();
                var t = _avatar.transform;
                while (t != null && instRoot != null && t != instRoot.transform)
                { segments.Insert(0, t.name); t = t.parent; }
                Transform src = temp.transform;
                foreach (var seg in segments)
                {
                    src = FindChild(src, seg);
                    if (src == null) break;
                }
                if (src == null) { _log = "在 prefab 中找不到与所选对象对应的节点。"; return; }

                var pairs = new List<(Transform dst, Transform src)>();
                CollectPairs(_avatar.transform, src, pairs);
                if (pairs.Count == 0) { _log = "prefab 与场景对象之间没有匹配到任何骨骼。"; return; }

                Undo.RecordObjects(pairs.ConvertAll(p => (UnityEngine.Object)p.dst).ToArray(), "恢复 Avatar 姿势");
                foreach (var (dst, s) in pairs)
                {
                    dst.localPosition = s.localPosition;
                    dst.localRotation = s.localRotation;
                    dst.localScale = s.localScale;
                }
                _log = $"✔ 已从 {prefabPath} 恢复出厂（T-）姿势（{pairs.Count} 个变换，可用 Ctrl+Z 撤销）。";
                SceneView.RepaintAll();
            }
            finally { if (temp != null) UnityEngine.Object.DestroyImmediate(temp); }
        }

        static Transform FindChild(Transform parent, string name)
        {
            foreach (Transform c in parent) if (c.name == name) return c;
            return null;
        }

        static void CollectPairs(Transform dst, Transform src, List<(Transform dst, Transform src)> pairs)
        {
            pairs.Add((dst, src));
            foreach (Transform c in dst)
            {
                var s = FindChild(src, c.name);
                if (s != null) CollectPairs(c, s, pairs);
            }
        }

        // ------------------------------------------------------------------
        // 转换
        // ------------------------------------------------------------------
        AnimationClip ConvertOne(AvatarRig rig, VpdPose pose, string clipName)
        {
            var rt = BuildRetargeter(rig, pose);
            rt.Apply(rig);
            AnimationClip clip = _mode == OutputMode.HumanoidMuscle
                ? AnimClipBuilder.BuildMuscleClip(rig, clipName)
                : AnimClipBuilder.BuildGenericClip(rig, rt, clipName);
            if (_extraBones) AnimClipBuilder.WriteExtraBoneCurves(clip, rig, pose, _mirror);
            return clip;
        }

        void GenerateSingle()
        {
            bool needHumanoid = _mode == OutputMode.HumanoidMuscle;
            var rig = AvatarRig.Create(_avatar, needHumanoid, out var err);
            if (rig == null) { _log = err; return; }
            try
            {
                var clip = ConvertOne(rig, _pose, _clipName);
                var path = SaveClip(clip, _outputFolder, _clipName);
                _log = $"✔ 已保存：{path}\n" + BuildRetargeter(rig, _pose).BuildReport(rig);
            }
            finally
            {
                // 绝不让场景 avatar 残留姿势——无论成功失败都恢复原状
                rig.RestoreRest();
                rig.Dispose();
            }
        }

        void GenerateBatch()
        {
            bool needHumanoid = _mode == OutputMode.HumanoidMuscle;
            var sb = new StringBuilder();
            int ok = 0, fail = 0;
            var rig = AvatarRig.Create(_avatar, needHumanoid, out var err);
            if (rig == null) { _log = err; return; }
            try
            {
                for (int i = 0; i < _batch.Count; i++)
                {
                    var item = _batch[i];
                    try
                    {
                        EditorUtility.DisplayProgressBar("VPD 批量转换", item.Name, (float)i / _batch.Count);
                        // 工程内资产已由 ScriptedImporter 完成 Shift-JIS 解码
                        var text = item.Asset != null ? item.Asset.text : VpdPose.Decode(File.ReadAllBytes(item.ExternalPath));
                        var pose = VpdPose.Parse(text);
                        rig.RestoreRest();
                        var clip = ConvertOne(rig, pose, item.Name);
                        var path = SaveClip(clip, _outputFolder, item.Name);
                        item.Status = "✔ " + path;
                        ok++;
                    }
                    catch (Exception ex) { item.Status = "✖ " + ex.Message; fail++; }
                }
            }
            finally
            {
                // 结束时把场景 avatar 还原到原（rest/T-）姿势
                rig.RestoreRest();
                rig.Dispose();
                EditorUtility.ClearProgressBar();
            }
            sb.AppendLine($"批量完成：成功 {ok} 个，失败 {fail} 个。输出目录：{_outputFolder}");
            foreach (var b in _batch) sb.AppendLine($"{b.Name}  →  {b.Status}");
            _log = sb.ToString();
        }

        string SaveClip(AnimationClip clip, string folder, string clipName)
        {
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets", StringComparison.Ordinal))
                throw new Exception("输出文件夹必须以 'Assets' 开头。");
            EnsureFolder(folder);
            var safeName = string.Join("_", clipName.Split(Path.GetInvalidFileNameChars()));
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{safeName}.anim");
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = false; settings.startTime = 0f; settings.stopTime = 1f / 60f;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(clip);
            return path;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
