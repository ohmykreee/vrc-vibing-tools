// Co-coded with Kimi K3 (max) in OpenCode

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PoseEditor
{
    /// <summary>
    /// Pose Editor —— 把 VRChat humanoid avatar 的姿势做成「Unity ↔ 外部 3D 程序（Blender）」的往返工作流。
    /// 菜单：Tools → Vibing Tools → Pose Editor (VRChat Humanoid)
    /// 依赖：Unity 官方 FBX Exporter 插件（com.unity.formats.fbx），未安装则弹窗引导并退出。
    ///
    /// 【导出 FBX（去 Blender）】
    ///   1. 指定 Humanoid avatar（场景对象或 prefab/模型资产均可）；
    ///   2. 指定一个 pose .anim（取第 0 帧应用到隐藏临时副本，原始对象从不被修改）；
    ///   3. 只导出用户选定（或自动检测）的 body mesh + 骨架，不导出材质等无关元素；
    ///      姿势烘焙成单帧动画随 FBX 导出；结束后弹窗引导去 Blender 修改姿势。
    ///
    /// 【FBX → Anim（回 Unity）】
    ///   不修改 FBX 的任何导入设置（避免 Humanoid 重定向失败丢动画）：
    ///   - FBX 已是 Humanoid：直接采样 muscle 曲线；
    ///   - FBX 是 Generic（默认）：按【骨骼名】把旋转曲线套到 avatar 骨架上
    ///    （hips 位移按 FBX 休息姿势与 avatar 休息姿势的比例做单位换算），
    ///     再用 HumanPoseHandler.GetHumanPose 提取 muscle，生成 humanoid anim。
    ///   多个动画时下拉选择；默认开启「只导出第 0 帧」。
    /// </summary>
    public class PoseEditor : EditorWindow
    {
        const string FbxExporterTypeName = "UnityEditor.Formats.Fbx.Exporter.ModelExporter";
        const string FbxSettingsTypeName = "UnityEditor.Formats.Fbx.Exporter.ExportModelSettingsSerialize";

        [MenuItem("Tools/Vibing Tools/Pose Editor (VRChat Humanoid)", priority = 25)]
        static void Open() => GetWindow<PoseEditor>("Pose Editor");

        // ---- 共享 ----
        [SerializeField] GameObject _avatar;                  // 两个页签共用（场景对象或 prefab 资产）

        // ---- 导出 ----
        [SerializeField] AnimationClip _clip;
        [SerializeField] SkinnedMeshRenderer _bodyMesh;       // 导出内容：body mesh
        [SerializeField] Transform _skeletonRoot;             // 导出内容：骨架根

        // ---- 导入 ----
        [SerializeField] GameObject _fbx;
        [SerializeField] int _fbxClipIndex;
        [SerializeField] bool _onlyFirstFrame = true;
        [SerializeField] bool _normalizeBoneNames = true;

        [SerializeField] bool[] _folds = new bool[5];
        [SerializeField] string _log =
            "【导出 FBX】选 avatar + pose .anim → 自动检测 body mesh/骨架 → 导出 FBX → 去 Blender 改姿势。\n" +
            "【FBX → Anim】拖入 Blender 改好的 FBX → 读取动画 → 生成 .anim（默认只取第 0 帧）。";

        int _tab;
        Vector2 _scroll, _helpScroll;
        AnimationClip[] _fbxClips = new AnimationClip[0];
        GameObject _prevAvatar;

        // ==================================================================
        // 启动检测：FBX Exporter 未安装 → 弹窗引导安装并退出
        // ==================================================================
        static bool? _fbxInstalled;
        static bool FbxExporterInstalled
        {
            get
            {
                if (!_fbxInstalled.HasValue)
                    _fbxInstalled = FindType(FbxExporterTypeName) != null;
                return _fbxInstalled.Value;
            }
        }

        void OnEnable()
        {
            if (FbxExporterInstalled) return;
            EditorApplication.delayCall += () =>
            {
                bool open = EditorUtility.DisplayDialog("Pose Editor",
                    "本工具的导出功能依赖 Unity 官方 FBX Exporter 插件（com.unity.formats.fbx），当前未安装。\n\n" +
                    "安装方法：Window → Package Manager → 左上角 Packages 选「Unity Registry」\n" +
                    "→ 搜索 “FBX Exporter” → Install。安装完成后重新打开本工具。",
                    "打开 Package Manager", "关闭并退出");
                if (open) EditorApplication.ExecuteMenuItem("Window/Package Manager");
                Close();
            };
        }

        // ==================================================================
        void OnGUI()
        {
            if (!FbxExporterInstalled)
            {
                EditorGUILayout.HelpBox("未检测到 FBX Exporter 插件（com.unity.formats.fbx）。请安装后重新打开本工具。", MessageType.Error);
                return;
            }

            // avatar 变化 → 重新自动检测导出目标
            if (_avatar != _prevAvatar)
            {
                _prevAvatar = _avatar;
                _bodyMesh = null;
                _skeletonRoot = null;
                if (_avatar != null && ValidateAvatar(_avatar) == null) AutoDetectExportTargets();
            }

            EditorGUILayout.Space();
            _tab = GUILayout.Toolbar(_tab, new[] { "导出 FBX（去 Blender）", "FBX → Anim（回 Unity）", "使用说明" });
            EditorGUILayout.Space();

            switch (_tab)
            {
                case 0: DrawExportTab(); break;
                case 1: DrawImportTab(); break;
                default: DrawHelp(); break;
            }

            if (_tab != 2)
            {
                EditorGUILayout.Space();
                DrawLog();
            }
        }

        // ------------------------------------------------------------------
        // 页签 1：导出 FBX
        // ------------------------------------------------------------------
        void DrawExportTab()
        {
            EditorGUILayout.LabelField("1. VRChat Avatar（Humanoid，场景对象或 prefab 资产均可）", EditorStyles.boldLabel);
            DrawAvatarField();
            if (_avatar == null)
            {
                EditorGUILayout.HelpBox("场景 Hierarchy 里的对象、或 Project 里的 prefab/模型资产都可以。\n姿势只应用在隐藏临时副本上，原始对象不会被修改。", MessageType.Info);
            }
            else
            {
                var err = ValidateAvatar(_avatar);
                if (err != null) EditorGUILayout.HelpBox(err, MessageType.Error);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. Pose .anim（套用其第 0 帧）", EditorStyles.boldLabel);
            _clip = (AnimationClip)EditorGUILayout.ObjectField(_clip, typeof(AnimationClip), false);
            if (_clip == null)
                EditorGUILayout.HelpBox("选择一个单帧 pose .anim（例如 Vpd2Anim 生成的姿势文件）。多帧动画只取第 0 帧。", MessageType.Info);
            else if (!_clip.humanMotion)
                EditorGUILayout.HelpBox("该 .anim 不是 humanoid muscle 动画（generic transform 曲线），只能套用在骨骼路径完全相同的 avatar 上。", MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. 导出内容（仅 body mesh + 骨架，不导出材质）", EditorStyles.boldLabel);
            _bodyMesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                new GUIContent("Body Mesh", "要导出的身体网格（SkinnedMeshRenderer）。"),
                _bodyMesh, typeof(SkinnedMeshRenderer), true);
            using (new EditorGUILayout.HorizontalScope())
            {
                _skeletonRoot = (Transform)EditorGUILayout.ObjectField(
                    new GUIContent("骨架根", "骨架的根节点（如 Armature 或 hips），只导出其子树。"),
                    _skeletonRoot, typeof(Transform), true);
                using (new EditorGUI.DisabledScope(_avatar == null || ValidateAvatar(_avatar) != null))
                    if (GUILayout.Button("自动检测", GUILayout.Width(70))) AutoDetectExportTargets();
            }
            DrawExportTargetStatus();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("4. 导出", EditorStyles.boldLabel);
            bool ready = _avatar != null && _clip != null && ValidateAvatar(_avatar) == null;
            using (new EditorGUI.DisabledScope(!ready))
                if (GUILayout.Button("导出 FBX（body mesh + 骨架 + 姿势动画）", GUILayout.Height(26)))
                    ExportPoseFbx();
            EditorGUILayout.LabelField("导出后原始 avatar 保持初始（T-）姿势——它从未被修改。", EditorStyles.miniLabel);
        }

        void DrawExportTargetStatus()
        {
            if (_avatar == null || _bodyMesh == null && _skeletonRoot == null) return;
            if (_bodyMesh != null && !UnderAvatar(_bodyMesh.transform))
                EditorGUILayout.HelpBox("Body Mesh 不属于所选 avatar。", MessageType.Error);
            if (_skeletonRoot != null && !UnderAvatar(_skeletonRoot))
                EditorGUILayout.HelpBox("骨架根不属于所选 avatar。", MessageType.Error);
            if (_bodyMesh != null && _skeletonRoot != null && UnderAvatar(_bodyMesh.transform) && UnderAvatar(_skeletonRoot))
            {
                int outside = _bodyMesh.bones.Count(b => b != null && b != _skeletonRoot && !b.IsChildOf(_skeletonRoot));
                if (outside > 0)
                    EditorGUILayout.HelpBox($"⚠ body mesh 有 {outside} 根骨骼不在所选骨架子树下，导出结果的皮肤可能损坏。请改选更高的骨架根。", MessageType.Warning);
                else
                    EditorGUILayout.LabelField($"✔ {_bodyMesh.name}（{_bodyMesh.bones.Length} 根骨骼） + 骨架 {_skeletonRoot.name}", EditorStyles.miniLabel);
            }
        }

        // ------------------------------------------------------------------
        // 页签 2：FBX → Anim
        // ------------------------------------------------------------------
        void DrawImportTab()
        {
            EditorGUILayout.LabelField("1. Blender 修改后导出的 FBX", EditorStyles.boldLabel);
            var newFbx = (GameObject)EditorGUILayout.ObjectField(_fbx, typeof(GameObject), false);
            if (newFbx != _fbx) { _fbx = newFbx; _fbxClips = new AnimationClip[0]; _fbxClipIndex = 0; }
            if (_fbx == null)
                EditorGUILayout.HelpBox("把 Blender 导出的 .fbx（已放进工程 Assets 内）拖到这里。\n保持默认（Generic）导入设置即可，本工具不会修改 FBX 的 Rig 设置。", MessageType.Info);
            else if (FbxAssetPath() == null)
                EditorGUILayout.HelpBox("请选择 .fbx 模型资产。", MessageType.Error);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. Avatar（Humanoid，用于骨骼匹配与 muscle 提取）", EditorStyles.boldLabel);
            DrawAvatarField();
            EditorGUILayout.LabelField("FBX 动画是 generic transform 格式时必需；FBX 已是 Humanoid 则不需要。", EditorStyles.miniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. 读取动画并生成", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(FbxAssetPath() == null))
                if (GUILayout.Button("读取动画列表")) LoadFbxClips();

            _onlyFirstFrame = EditorGUILayout.Toggle(new GUIContent("只导出第 0 帧（单帧 pose）",
                "开启（默认）：生成单帧 pose .anim。取第 0 帧；若第 0 帧近乎静止而 take 后部有明显姿势\n" +
                "（Blender Force Start Key 可能写入静止帧），自动改取摆动最大的时刻并提示。\n" +
                "关闭：完整转换/复制整个动画。"), _onlyFirstFrame);

            _normalizeBoneNames = EditorGUILayout.Toggle(new GUIContent("规范化骨骼名匹配（默认开）",
                "匹配骨骼名时：忽略大小写、把 '.' 与 '_' 视为相同、剥掉 Blender 重名后缀 .001。\n" +
                "FBX 往返常把骨骼名里的 '.' 转成 '_'（如 thigh.L → thigh_L），开启后可自动匹配上。"),
                _normalizeBoneNames);

            if (_fbxClips.Length > 0)
            {
                var names = _fbxClips.Select(c => c.name).ToArray();
                _fbxClipIndex = EditorGUILayout.Popup("选择动画", Mathf.Clamp(_fbxClipIndex, 0, names.Length - 1), names);
                var sel = _fbxClips[Mathf.Clamp(_fbxClipIndex, 0, _fbxClips.Length - 1)];
                EditorGUILayout.LabelField(
                    sel.humanMotion ? "humanoid muscle 动画 —— 直接采样" : "generic transform 动画 —— 采样到 FBX 自身骨架后提取 muscle",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("生成 .anim", GUILayout.Height(24))) GenerateAnimFromFbx();
            }
        }

        void DrawAvatarField()
        {
            _avatar = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("", "Humanoid avatar：场景对象或 prefab/模型资产均可。"),
                _avatar, typeof(GameObject), true);
        }

        void DrawLog()
        {
            EditorGUILayout.LabelField("操作日志", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(70));
            EditorGUILayout.TextArea(_log, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------
        // 使用说明
        // ------------------------------------------------------------------
        void DrawHelp()
        {
            _helpScroll = EditorGUILayout.BeginScrollView(_helpScroll);
            HelpFold(0, "快速上手（完整往返流程）",
                "【去 Blender】\n" +
                "1. 「导出 FBX」页：选 avatar（场景对象或 prefab 均可）+ pose .anim\n" +
                "2. 确认自动检测到的 Body Mesh 与骨架根（也可手动指定）\n" +
                "3. 点【导出 FBX】—— 只含 body mesh + 骨架 + 姿势动画，无材质\n\n" +
                "【在 Blender 里】\n" +
                "4. File → Import → FBX（默认设置），姿势在动画第 0 帧\n" +
                "5. 修改姿势，给改动过的骨骼插入关键帧（建议全骨插帧）\n" +
                "6. File → Export → FBX，勾选 Bake Animation\n\n" +
                "【回 Unity】\n" +
                "7. 把导出的 .fbx 放进工程 Assets（保持默认 Generic 导入设置），拖入「FBX → Anim」页\n" +
                "8. 点【读取动画列表】，有多个动画时下拉选择\n" +
                "9. 保持【只导出第 0 帧】开启 →【生成 .anim】");
            HelpFold(1, "导出页说明 / 自动检测",
                "● 导出内容只有：选定的 body mesh（SkinnedMeshRenderer）+ 选定的骨架子树\n" +
                "  + 单帧姿势动画。衣服/道具/眼睛等其他网格、VRC 组件、动态骨、材质\n" +
                "  一律不导出（若 FBX Exporter 不接受空材质，会自动回退为材质占位符）。\n" +
                "● 自动检测规则：\n" +
                "  · Body Mesh：名字含 body 优先，其次包含 hips 骨骼、骨骼数最多者；\n" +
                "  · 骨架根：从 hips 向上，最后一个仍包含全部 body 骨骼的祖先（不越过 avatar 根）。\n" +
                "● 原始 avatar 从不被修改：姿势应用在隐藏临时副本上，导出后它自然保持\n" +
                "  初始（T-）姿势。\n" +
                "● pose .anim：humanoid muscle（推荐）或 generic transform（仅限同路径骨架），\n" +
                "  均取第 0 帧。");
            HelpFold(2, "Blender 侧操作要点",
                "● 导入：File → Import → FBX，全部默认设置即可\n" +
                "  （不要勾 Automatic Bone Orientation，会改变骨骼静止朝向）。\n" +
                "● 改姿势：Pose Mode 摆好后，选中相关骨骼按 I 插入关键帧\n" +
                "  （建议在第 0 帧给全部骨骼插 LocRotScale，最稳）。\n" +
                "● 不要移动/缩放 Armature 物体本身，不要改骨骼名字。\n" +
                "● 导出：File → Export → FBX，勾上 Bake Animation。\n" +
                "● 只关心骨架姿势：mesh 变形/材质修改不会被带回 Unity。");
            HelpFold(3, "导入页说明（FBX → Anim）",
                "● 本工具不会修改 FBX 的 Rig 导入设置——保持 Unity 默认（Generic）即可。\n" +
                "● FBX 是 Generic（默认）：动画是 transform 曲线。工具把动画【按路径精确\n" +
                "  采样到 FBX 自身的骨架实例上】（动画与该 FBX 同源，路径必然匹配），\n" +
                "  再用 HumanPoseHandler（avatar 的 Avatar + FBX 骨架根）按骨骼名绑定\n" +
                "  并提取 muscle。只要求 avatar 的 humanoid 骨骼【名字】在 FBX 骨架中存在。\n" +
                "● 【规范化骨骼名匹配】（默认开）：骨骼名的大小写差异、'.' 与 '_' 差异、\n" +
                "  Blender 重名后缀 .001 都会被自动修正——FBX 往返常把骨骼名里的 '.'\n" +
                "  转成 '_'（如 thigh.L → thigh_L），开着它就能正确匹配。\n" +
                "● 若提示「找不到必需骨骼 / 绑定失败」：说明导入页选的 avatar 与当初\n" +
                "  导出 FBX 时用的不是同一套骨骼命名——请换用导出时的那个 avatar。\n" +
                "● hips 高度按「FBX 骨架静止高度 vs avatar 静止高度」自动做单位换算。\n" +
                "● FBX 已是 Humanoid：直接采样 muscle 曲线，无需指定 avatar。\n" +
                "● FBX 内有多个动画（take/action）时，在【选择动画】下拉里挑一个。\n" +
                "● 【只导出第 0 帧】（默认开）：生成单帧 pose。取第 0 帧；若第 0 帧近乎静止\n" +
                "  （Blender Force Start Key 可能写入静止帧、姿势在末尾帧），自动改取摆动\n" +
                "  最大的时刻并在日志提示；关闭则转换/复制整个动画。\n" +
                "● 输出的 humanoid muscle .anim 可直接用于 VRChat 手势层 / Pose Bank。");
            HelpFold(4, "依赖与注意事项",
                "● 依赖 Unity 官方 FBX Exporter（com.unity.formats.fbx）：\n" +
                "  未安装时打开本工具会弹窗引导安装并退出。\n" +
                "● 输出的 anim 只记录 humanoid muscle 骨骼；头发/衣服/附件等\n" +
                "  非 humanoid 骨骼的姿势不会被带回。\n" +
                "● avatar 的导入设置必须是 Rig → Animation Type → Humanoid。\n" +
                "● 若生成结果姿势不对：检查 Blender 侧是否改过骨骼名、动过 Armature 物体，\n" +
                "  或导入时勾了 Automatic Bone Orientation。");
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
        // Avatar 校验 / Animator 解析
        // ------------------------------------------------------------------
        static Animator ResolveAnimator(GameObject go)
        {
            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = go.GetComponentInParent<Animator>();
            if (anim == null) anim = go.GetComponentInChildren<Animator>();
            return anim;
        }

        static string ValidateAvatar(GameObject go)
        {
            if (go == null) return "请先指定 avatar（场景对象或 prefab/模型资产均可）。";
            var anim = ResolveAnimator(go);
            if (anim == null) return "avatar 上没有 Animator 组件。";
            if (!anim.isHuman)
                return "avatar 的 Rig 不是 Humanoid。请在模型导入设置 Rig → Animation Type → Humanoid。";
            if (anim.avatar == null) return "avatar 的 Animator 上没有 Avatar。";
            return null;
        }

        bool UnderAvatar(Transform t) => _avatar != null && (t == _avatar.transform || t.IsChildOf(_avatar.transform));

        // ==================================================================
        // 导出：avatar + pose .anim → FBX（仅 body mesh + 骨架，无材质）
        // ==================================================================
        void ExportPoseFbx()
        {
            var err = ValidateAvatar(_avatar);
            if (err != null) { _log = err; return; }
            if (_clip == null) { _log = "请先选择要套用的 pose .anim。"; return; }
            if (_bodyMesh == null || _skeletonRoot == null) AutoDetectExportTargets();
            if (_bodyMesh == null) { _log = "找不到 body mesh（avatar 下没有 SkinnedMeshRenderer）。请手动指定。"; return; }
            if (_skeletonRoot == null) { _log = "无法确定骨架根（hips 未映射）。请手动指定。"; return; }
            if (!UnderAvatar(_bodyMesh.transform) || !UnderAvatar(_skeletonRoot))
            { _log = "Body Mesh / 骨架根必须属于所选 avatar。"; return; }

            var path = EditorUtility.SaveFilePanelInProject(
                "导出 FBX（body mesh + 骨架 + 姿势动画）", _clip.name, "fbx",
                "选择 FBX 保存位置（Assets 内）");
            if (string.IsNullOrEmpty(path)) return;

            GameObject work = null, clean = null;
            try
            {
                // 1) 隐藏临时副本：原始对象从不被修改 → 天然保持初始（T-）姿势
                work = CreateWorkInstance(_avatar);
                if (work == null) { _log = "创建临时副本失败。"; return; }

                // 2) 应用姿势（第 0 帧）
                var anim = ResolveAnimator(work);
                ApplyPose(anim, _clip);

                // 3) 把用户在原始 avatar 上选定的 body mesh / 骨架映射到临时副本
                var smrT = MapToWork(work, _bodyMesh.transform);
                var workSmr = smrT != null ? smrT.GetComponent<SkinnedMeshRenderer>() : null;
                var workSkel = MapToWork(work, _skeletonRoot);
                if (workSmr == null || workSkel == null)
                { _log = "在临时副本中找不到对应的 body mesh / 骨架节点。"; return; }

                int outside = _bodyMesh.bones.Count(b => b != null && b != _skeletonRoot && !b.IsChildOf(_skeletonRoot));

                var absDir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(absDir)) Directory.CreateDirectory(absDir);

                // 4) 先试不导出材质；FBX Exporter 不接受空材质则回退为材质占位符
                string exported, note, ferr;
                clean = BuildCleanExportRoot(workSmr, workSkel, _avatar.name, true);
                AttachPoseClip(clean, _clip.name);
                exported = FbxExport(path, new Object[] { clean }, out ferr);
                if (!string.IsNullOrEmpty(exported))
                {
                    note = "（未导出材质）";
                }
                else
                {
                    var firstErr = ferr;
                    Object.DestroyImmediate(clean); clean = null;
                    clean = BuildCleanExportRoot(workSmr, workSkel, _avatar.name, false);
                    AttachPoseClip(clean, _clip.name);
                    exported = FbxExport(path, new Object[] { clean }, out ferr);
                    note = string.IsNullOrEmpty(exported) ? null : "（FBX Exporter 不接受空材质，已保留材质占位符）";
                    if (note == null && !string.IsNullOrEmpty(firstErr)) ferr = firstErr + "\n" + ferr;
                }
                if (string.IsNullOrEmpty(exported)) { _log = "FBX 导出失败：" + (ferr ?? "未知错误"); return; }

                AssetDatabase.ImportAsset(path);
                var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (fbxAsset != null) EditorGUIUtility.PingObject(fbxAsset);

                _log = $"✔ 已导出：{exported} {note}\n" +
                       $"内容：body mesh「{_bodyMesh.name}」+ 骨架「{_skeletonRoot.name}」+ 姿势动画（第 0 帧单帧）。\n" +
                       (outside > 0 ? $"⚠ 有 {outside} 根蒙皮骨骼不在骨架子树下，皮肤可能损坏。\n" : "") +
                       "原始 avatar 未被修改，保持初始（T-）姿势。\n" +
                       "下一步：在 Blender 中导入该 FBX 修改姿势（详见弹窗与「使用说明」）。";
                EditorUtility.DisplayDialog("Pose Editor — 导出完成",
                    "FBX 已导出：\n" + exported + "\n\n" +
                    "接下来请在外部 3D 程序（如 Blender）中修改姿势：\n" +
                    "1. File → Import → FBX 导入该文件（默认设置，姿势在动画第 0 帧）；\n" +
                    "2. 修改姿势，并给改动过的骨骼插入关键帧（建议全骨插帧）；\n" +
                    "3. File → Export → FBX 导出，勾选 Bake Animation（不要改骨骼名）；\n" +
                    "4. 回到本工具「FBX → Anim」页，从导出的 FBX 生成 .anim。", "好");
            }
            catch (Exception ex) { _log = "导出失败：\n" + ex.Message; }
            finally
            {
                if (clean != null) Object.DestroyImmediate(clean);
                if (work != null) Object.DestroyImmediate(work);
            }
        }

        /// <summary>把原始 avatar 上的节点映射到临时副本（按相对路径）。</summary>
        Transform MapToWork(GameObject workRoot, Transform orig)
        {
            if (orig == null) return null;
            if (orig == _avatar.transform) return workRoot.transform;
            var path = AnimationUtility.CalculateTransformPath(orig, _avatar.transform);
            return workRoot.transform.Find(path);
        }

        /// <summary>隐藏临时副本（prefab 资产 → InstantiatePrefab；场景对象 → 普通拷贝）。</summary>
        static GameObject CreateWorkInstance(GameObject source)
        {
            GameObject inst = PrefabUtility.IsPartOfPrefabAsset(source)
                ? (GameObject)PrefabUtility.InstantiatePrefab(source)
                : Object.Instantiate(source);
            if (inst == null) return null;
            inst.hideFlags = HideFlags.HideAndDontSave;
            inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            return inst;
        }

        // ------------------------------------------------------------------
        // 自动检测导出目标
        // ------------------------------------------------------------------
        void AutoDetectExportTargets()
        {
            var anim = ResolveAnimator(_avatar);
            if (anim == null) return;
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips);

            // Body Mesh：名字含 body 优先，其次包含 hips 骨骼，再按骨骼数
            SkinnedMeshRenderer best = null;
            int bestScore = -1;
            foreach (var smr in anim.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                int score = smr.bones.Length;
                if (hips != null && Array.IndexOf(smr.bones, hips) >= 0) score += 10000;
                if (smr.name.ToLowerInvariant().Contains("body")) score += 100000;
                if (score > bestScore) { bestScore = score; best = smr; }
            }
            _bodyMesh = best;

            // 骨架根：从 hips 向上，最后一个仍包含全部 body 骨骼的祖先（不越过 avatar 根）
            _skeletonRoot = hips;
            if (hips != null && best != null)
            {
                var a = hips.parent;
                while (a != null && a != anim.transform && a != _avatar.transform)
                {
                    if (AllBonesUnder(a, best)) _skeletonRoot = a;
                    a = a.parent;
                }
                // avatar 根本身不参与（骨架根必须是其下的节点）
            }
        }

        static bool AllBonesUnder(Transform root, SkinnedMeshRenderer smr)
        {
            foreach (var b in smr.bones)
                if (b != null && b != root && !b.IsChildOf(root)) return false;
            return true;
        }

        // ------------------------------------------------------------------
        // 应用姿势（第 0 帧）—— humanoid 走 muscle（参考 Vpd2Anim 的 HumanPoseHandler 用法），
        // generic 走 transform 曲线按路径直接写骨骼。
        // ------------------------------------------------------------------
        static void ApplyPose(Animator anim, AnimationClip clip)
        {
            if (clip.humanMotion)
            {
                var hp = new HumanPose
                {
                    bodyPosition = Vector3.zero,
                    bodyRotation = Quaternion.identity,
                    muscles = new float[HumanTrait.MuscleCount]
                };
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    if (b.path.Length != 0 || b.type != typeof(Animator)) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null) continue;
                    float v = curve.Evaluate(0f);
                    switch (b.propertyName)
                    {
                        case "RootT.x": hp.bodyPosition.x = v; break;
                        case "RootT.y": hp.bodyPosition.y = v; break;
                        case "RootT.z": hp.bodyPosition.z = v; break;
                        case "RootQ.x": hp.bodyRotation.x = v; break;
                        case "RootQ.y": hp.bodyRotation.y = v; break;
                        case "RootQ.z": hp.bodyRotation.z = v; break;
                        case "RootQ.w": hp.bodyRotation.w = v; break;
                        default:
                            int mi = MuscleIndex(b.propertyName);
                            if (mi >= 0 && mi < hp.muscles.Length) hp.muscles[mi] = v;
                            break;
                    }
                }
                using (var handler = new HumanPoseHandler(anim.avatar, anim.transform))
                    handler.SetHumanPose(ref hp);
            }
            else
            {
                var root = anim.transform;
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    if (b.type != typeof(Transform)) continue;
                    var t = root.Find(b.path);
                    if (t == null) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null) continue;
                    float v = curve.Evaluate(0f);
                    switch (b.propertyName)
                    {
                        case "m_LocalPosition.x": { var p = t.localPosition; p.x = v; t.localPosition = p; break; }
                        case "m_LocalPosition.y": { var p = t.localPosition; p.y = v; t.localPosition = p; break; }
                        case "m_LocalPosition.z": { var p = t.localPosition; p.z = v; t.localPosition = p; break; }
                        case "m_LocalRotation.x": { var q = t.localRotation; q.x = v; t.localRotation = q; break; }
                        case "m_LocalRotation.y": { var q = t.localRotation; q.y = v; t.localRotation = q; break; }
                        case "m_LocalRotation.z": { var q = t.localRotation; q.z = v; t.localRotation = q; break; }
                        case "m_LocalRotation.w": { var q = t.localRotation; q.w = v; t.localRotation = q; break; }
                        case "m_LocalScale.x":    { var s = t.localScale; s.x = v; t.localScale = s; break; }
                        case "m_LocalScale.y":    { var s = t.localScale; s.y = v; t.localScale = s; break; }
                        case "m_LocalScale.z":    { var s = t.localScale; s.z = v; t.localScale = s; break; }
                    }
                }
            }
        }

        static Dictionary<string, int> _muscleIndex;
        static int MuscleIndex(string name)
        {
            if (_muscleIndex == null)
            {
                _muscleIndex = new Dictionary<string, int>();
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    _muscleIndex[HumanTrait.MuscleName[i]] = i;
            }
            return _muscleIndex.TryGetValue(name, out var idx) ? idx : -1;
        }

        // ------------------------------------------------------------------
        // 干净导出层级：骨架子树（纯 Transform）+ 指定 body mesh。
        // 衣服/道具等其他网格、VRC 组件、动态骨一律不带；材质默认置空。
        // ------------------------------------------------------------------
        static GameObject BuildCleanExportRoot(SkinnedMeshRenderer srcSmr, Transform srcSkelRoot, string rootName, bool stripMaterials)
        {
            var go = new GameObject(rootName);
            go.hideFlags = HideFlags.HideAndDontSave;
            var root = go.transform;

            // 骨架子树（带 Renderer 的节点是网格，不作为骨骼克隆）
            var map = new Dictionary<Transform, Transform>();
            CloneBones(srcSkelRoot, root, map);

            // body mesh 节点（挂导出根下，保留原本地 TRS）
            var meshGO = new GameObject(srcSmr.name);
            meshGO.hideFlags = HideFlags.HideAndDontSave;
            var meshT = meshGO.transform;
            meshT.SetParent(root, false);
            meshT.localPosition = srcSmr.transform.localPosition;
            meshT.localRotation = srcSmr.transform.localRotation;
            meshT.localScale = srcSmr.transform.localScale;

            var dst = meshGO.AddComponent<SkinnedMeshRenderer>();
            dst.sharedMesh = srcSmr.sharedMesh;
            dst.localBounds = srcSmr.localBounds;
            dst.updateWhenOffscreen = srcSmr.updateWhenOffscreen;
            dst.sharedMaterials = stripMaterials ? new Material[0] : srcSmr.sharedMaterials;
            dst.rootBone = srcSmr.rootBone != null && map.TryGetValue(srcSmr.rootBone, out var rb) ? rb : null;
            var srcBones = srcSmr.bones;
            var dstBones = new Transform[srcBones.Length];
            for (int i = 0; i < srcBones.Length; i++)
                if (srcBones[i] != null) map.TryGetValue(srcBones[i], out dstBones[i]);
            dst.bones = dstBones;
            return go;
        }

        static void CloneBones(Transform src, Transform dstParent, Dictionary<Transform, Transform> map)
        {
            var go = new GameObject(src.name);
            go.hideFlags = HideFlags.HideAndDontSave;
            var t = go.transform;
            t.SetParent(dstParent, false);
            t.localPosition = src.localPosition;
            t.localRotation = src.localRotation;
            t.localScale = src.localScale;
            map.Add(src, t);
            foreach (Transform c in src)
            {
                if (c.GetComponent<Renderer>() != null) continue;   // 网格节点不作为骨骼
                CloneBones(c, t, map);
            }
        }

        /// <summary>把干净层级的当前姿势烘焙成单帧 transform clip（给 FBX Exporter 当动画导出）。</summary>
        static AnimationClip BakePoseClip(GameObject cleanRoot, string clipName)
        {
            var clip = new AnimationClip { name = clipName, frameRate = 60f, legacy = true };
            var root = cleanRoot.transform;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                var path = AnimationUtility.CalculateTransformPath(t, root);
                var p = t.localPosition;
                var q = t.localRotation.normalized;
                var s = t.localScale;
                SetConst(clip, path, "m_LocalPosition.x", p.x);
                SetConst(clip, path, "m_LocalPosition.y", p.y);
                SetConst(clip, path, "m_LocalPosition.z", p.z);
                SetConst(clip, path, "m_LocalRotation.x", q.x);
                SetConst(clip, path, "m_LocalRotation.y", q.y);
                SetConst(clip, path, "m_LocalRotation.z", q.z);
                SetConst(clip, path, "m_LocalRotation.w", q.w);
                SetConst(clip, path, "m_LocalScale.x", s.x);
                SetConst(clip, path, "m_LocalScale.y", s.y);
                SetConst(clip, path, "m_LocalScale.z", s.z);
            }
            return clip;
        }

        static void AttachPoseClip(GameObject root, string clipName)
        {
            var poseClip = BakePoseClip(root, clipName);
            var legacy = root.AddComponent<Animation>();
            legacy.AddClip(poseClip, poseClip.name);
            legacy.clip = poseClip;
            legacy.playAutomatically = false;
        }

        static void SetConst(AnimationClip clip, string path, string prop, float v) =>
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(path, typeof(Transform), prop),
                AnimationCurve.Constant(0f, 1f / 60f, v));

        // ------------------------------------------------------------------
        // FBX Exporter（com.unity.formats.fbx）反射调用：
        // 优先带 ExportModelSettingsSerialize 的重载（显式 Include = ModelAndAnim），
        // 失败则退回基础重载。插件未必安装，不能直接引用其程序集。
        // ------------------------------------------------------------------
        static string FbxExport(string path, Object[] objects, out string error)
        {
            error = null;
            var exporter = FindType(FbxExporterTypeName);
            if (exporter == null) { error = "未安装 FBX Exporter（com.unity.formats.fbx）。"; return null; }

            var settingsType = FindType(FbxSettingsTypeName);
            if (settingsType != null)
            {
                try
                {
                    var settings = Activator.CreateInstance(settingsType);
                    var includeEnum = settingsType.GetNestedType("Include", BindingFlags.Public);
                    if (includeEnum != null)
                        TrySetMember(settings, "IncludeSetting", Enum.Parse(includeEnum, "ModelAndAnim"));
                    var m = exporter.GetMethod("ExportObjects", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string), typeof(Object[]), settingsType }, null);
                    if (m != null)
                        return m.Invoke(null, new object[] { path, objects, settings }) as string;
                }
                catch { /* 落到基础重载 */ }
            }

            try
            {
                var m2 = exporter.GetMethod("ExportObjects", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string), typeof(Object[]) }, null);
                if (m2 == null) { error = "FBX Exporter 版本不兼容：找不到 ModelExporter.ExportObjects。"; return null; }
                return m2.Invoke(null, new object[] { path, objects }) as string;
            }
            catch (Exception ex)
            {
                error = ex is TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException.Message : ex.Message;
                return null;
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        static void TrySetMember(object target, string name, object value)
        {
            var t = target.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(target, value, null); return; }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(target, value);
        }

        // ==================================================================
        // 导入：Blender 修改后的 FBX → anim
        // 不修改 FBX 的任何导入设置（Humanoid 重定向失败会丢动画，绝不触发）。
        // ==================================================================
        string FbxAssetPath()
        {
            if (_fbx == null) return null;
            var p = AssetDatabase.GetAssetPath(_fbx);
            if (string.IsNullOrEmpty(p) || !p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return null;
            return p;
        }

        void LoadFbxClips()
        {
            _fbxClips = new AnimationClip[0];
            _fbxClipIndex = 0;
            var path = FbxAssetPath();
            if (path == null) { _log = "请先选择 .fbx 模型资产。"; return; }

            var clips = AssetDatabase.LoadAllAssetsAtPath(path)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                .ToArray();
            if (clips.Length == 0)
            {
                _log = "该 FBX 不含任何动画。请确认 Blender 导出时勾选了 Bake Animation（并给骨骼插了关键帧）。\n" +
                       "注意：不要把这个 FBX 的 Rig 手动改成 Humanoid + CopyFromOther——若已改过，改回 Generic 并 Reimport 即可找回动画。";
                return;
            }
            _fbxClips = clips;
            // 诊断：每个 take 在第 0 帧相对 FBX 默认姿势摆动了几根骨骼
            // （纯曲线求值，不经采样）——一眼看出哪个 take 真的含姿势。
            GameObject probe = null;
            try
            {
                probe = (GameObject)Object.Instantiate(_fbx);
                if (probe != null) probe.hideFlags = HideFlags.HideAndDontSave;
                var probeRoot = probe != null ? probe.transform : null;
                _log = $"✔ 读取到 {clips.Length} 个动画（［］= 相对 FBX 默认姿势摆动的骨骼数）：\n" +
                    string.Join("\n", clips.Select(c =>
                    {
                        if (probeRoot == null || c.humanMotion)
                            return c.humanMotion
                                ? $"· {c.name}（humanoid muscle，{c.length:F2}s）"
                                : $"· {c.name}（generic transform，{c.length:F2}s）";
                        ScanDeviation(c, probeRoot, out int dev0, out int devMax, out float bestTime);
                        return $"· {c.name}（generic，{c.length:F2}s）［t=0 摆动 {dev0} 骨，max {devMax} 骨@t={bestTime:F2}s］";
                    }));
            }
            finally { if (probe != null) Object.DestroyImmediate(probe); }
        }

        void GenerateAnimFromFbx()
        {
            if (_fbxClips.Length == 0) { _log = "请先【读取动画列表】。"; return; }
            _fbxClipIndex = Mathf.Clamp(_fbxClipIndex, 0, _fbxClips.Length - 1);
            var src = _fbxClips[_fbxClipIndex];

            AnimationClip output;
            string defaultName;
            string report = null;
            if (src.humanMotion)
            {
                if (_onlyFirstFrame)
                {
                    defaultName = src.name + "_pose";
                    output = BuildFrame0Clip(src, defaultName);
                }
                else
                {
                    output = new AnimationClip();
                    EditorUtility.CopySerialized(src, output);
                    output.name = src.name;
                    defaultName = src.name;
                }
            }
            else
            {
                var err = ValidateAvatar(_avatar);
                if (err != null)
                { _log = "该动画是 generic transform 格式，需要指定 avatar 做骨骼匹配与 muscle 提取：\n" + err; return; }
                defaultName = _onlyFirstFrame ? src.name + "_pose" : src.name + "_muscle";
                output = RetargetGenericClip(src, defaultName, out report);
                if (output == null) { _log = "重定向失败：" + report; return; }
            }

            var path = EditorUtility.SaveFilePanelInProject("保存 Anim", defaultName, "anim", "选择 .anim 保存位置");
            if (string.IsNullOrEmpty(path)) { Object.DestroyImmediate(output); return; }

            output.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(output, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(output);
            _log = $"✔ 已生成：{path}\n（来源：{src.name}" +
                   (_onlyFirstFrame ? "，第 0 帧单帧）。" : "，完整动画）。") +
                   (report != null ? "\n" + report : "") +
                   (src.humanMotion || report != null ? "\nhumanoid muscle 格式，可直接用于 VRChat 手势层 / Pose Bank。" : "");
        }

        /// <summary>humanoid clip → 所有浮点曲线在 t=0 采样成常量单帧 pose。</summary>
        static AnimationClip BuildFrame0Clip(AnimationClip src, string name)
        {
            var output = new AnimationClip { name = name, frameRate = 60f };
            foreach (var b in AnimationUtility.GetCurveBindings(src))
            {
                var curve = AnimationUtility.GetEditorCurve(src, b);
                if (curve == null) continue;
                AnimationUtility.SetEditorCurve(output, b,
                    AnimationCurve.Constant(0f, 1f / 60f, curve.Evaluate(0f)));
            }
            var st = AnimationUtility.GetAnimationClipSettings(output);
            st.loopTime = false; st.startTime = 0f; st.stopTime = 1f / 60f;
            AnimationUtility.SetAnimationClipSettings(output, st);
            return output;
        }

        // ------------------------------------------------------------------
        // generic transform clip → humanoid muscle clip：
        // 双实例分离职责（采样要原始名、提取要 avatar 名，改名会破坏路径绑定）：
        //   sampleInst（原始骨骼名）— AnimationMode.SampleAnimationClip 采样
        //     （Animation 窗口预览同款引擎；AnimationClip.SampleAnimation 只兼容
        //       legacy 动画系统，对导入的非 legacy clip 会静默无操作，不能用）；
        //   extractInst（规范化为 avatar 骨骼名）— 逐帧 TRS 拷贝后，
        //     HumanPoseHandler（avatar 的 Avatar）按名绑定提取 muscle。
        // debug 输出：t=0/T/2/T 采样位移探测 + frame0 max|muscle|，便于定位问题。
        // ------------------------------------------------------------------
        AnimationClip RetargetGenericClip(AnimationClip src, string newName, out string report)
        {
            report = null;
            var anim = ResolveAnimator(_avatar);
            if (anim == null || anim.avatar == null)
            { report = "avatar 上没有可用的 Animator/Avatar。"; return null; }

            GameObject sampleInst = null, extractInst = null;
            bool startedMode = false;
            try
            {
                // 1) 两个 FBX 骨架实例（隐藏）：
                //    sampleInst 保持原始骨骼名（与 clip 曲线路径一致，用于采样）；
                //    extractInst 改名为 avatar 骨骼名（用于 HumanPoseHandler 按名绑定提取）。
                //    ——采样需要原始名、提取需要 avatar 名，改名会破坏路径绑定，故分离。
                sampleInst = (GameObject)Object.Instantiate(_fbx);
                extractInst = (GameObject)Object.Instantiate(_fbx);
                if (sampleInst == null || extractInst == null) { report = "实例化 FBX 失败。"; return null; }
                sampleInst.hideFlags = HideFlags.HideAndDontSave;
                extractInst.hideFlags = HideFlags.HideAndDontSave;
                sampleInst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                extractInst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                // 同一 prefab，遍历顺序一致 → 按索引一一对应（用于逐帧 TRS 拷贝）
                var sampleNodes = sampleInst.GetComponentsInChildren<Transform>(true);
                var extractNodes = extractInst.GetComponentsInChildren<Transform>(true);

                // 静止姿势快照（sampleInst，debug 位移测量的基准）
                var restRot = new Quaternion[sampleNodes.Length];
                var restPos = new Vector3[sampleNodes.Length];
                for (int i = 0; i < sampleNodes.Length; i++)
                { restRot[i] = sampleNodes[i].localRotation; restPos[i] = sampleNodes[i].localPosition; }

                // 2) 名称修正（extractInst）：把「规范化后与 avatar 相同」的节点改名为 avatar 的精确名
                //    （HumanPoseHandler 按名绑定需要精确名；规范化匹配选项开启时才执行）
                int renamed = RenameBonesToAvatarNames(extractInst.transform);

                // 3) 校验 humanoid 骨骼是否在 FBX 骨架中（extractInst，HumanPoseHandler 按名绑定的前提）
                var required = new[]
                {
                    HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
                    HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
                    HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg
                };
                var missingBones = new List<string>();
                int humanFound = 0, humanTotal = 0;
                Transform fbxHips = null;
                foreach (HumanBodyBones hbb in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (hbb == HumanBodyBones.LastBone) continue;
                    var bt = anim.GetBoneTransform(hbb);
                    if (bt == null) continue;
                    humanTotal++;
                    var found = FindByNameLoose(extractInst.transform, bt.name);
                    if (found != null)
                    {
                        humanFound++;
                        if (hbb == HumanBodyBones.Hips) fbxHips = found;
                    }
                    else if (Array.IndexOf(required, hbb) >= 0) missingBones.Add(bt.name);
                }
                if (missingBones.Count > 0)
                {
                    report = "无法把 avatar 的骨骼绑定到 FBX 骨架：找不到必需骨骼 " +
                             string.Join(", ", missingBones) + "（骨骼名不一致）。\n" +
                             "请确认导入页选择的 avatar 与当初导出 FBX 时使用的是同一个（同一套骨骼命名）。";
                    return null;
                }

                // 4) hips 静止高度比例 → 统一单位（bodyPosition 由 avatar humanScale 归一化）
                //    只缩放 extractInst：TRS 按本地值逐帧拷贝，世界高度由 extractInst 根缩放换算
                var avatarHips = anim.GetBoneTransform(HumanBodyBones.Hips);
                float ratio = 1f;
                bool scaled = false;
                if (avatarHips != null && fbxHips != null)
                {
                    float ah = avatarHips.position.y - anim.transform.position.y;
                    float fh = fbxHips.position.y - extractInst.transform.position.y;
                    if (Mathf.Abs(ah) > 1e-4f && Mathf.Abs(fh) > 1e-4f)
                    {
                        ratio = ah / fh;
                        if (Mathf.Abs(ratio - 1f) > 1e-3f)
                        {
                            extractInst.transform.localScale = extractInst.transform.localScale * ratio;
                            scaled = true;
                        }
                    }
                }

                // 5) AnimationMode 采样（Animation 窗口预览同款引擎，正确处理四元数/欧拉角）
                //    —— AnimationClip.SampleAnimation 只兼容 legacy 动画系统，对导入的
                //       非 legacy clip 会静默无操作，不能用。
                bool frame0 = _onlyFirstFrame;
                float fps = src.frameRate > 1e-3f ? src.frameRate : 60f;
                int frames = frame0 ? 1 : Mathf.Max(1, Mathf.CeilToInt(src.length * fps) + 1);

                // 单帧取哪一时刻：t=0 近乎静止而 take 后部有明显姿势时
                // （Blender Force Start Key 可能写入静止帧），自动改取摆动最大的时刻
                float poseTime = 0f;
                string poseNote = null;
                if (frame0)
                {
                    ScanDeviation(src, sampleInst.transform, out int dev0, out int devMax, out float bestTime);
                    if (dev0 < 3 && devMax > dev0 && bestTime > 1e-4f)
                    {
                        poseTime = bestTime;
                        poseNote = $"⚠ 第 0 帧仅 {dev0} 骨摆动（近乎静止），已自动改取 t={bestTime:F2}s（{devMax} 骨摆动）。";
                    }
                }
                var times = new List<float>(frames);
                var muscleVals = new List<float[]>(frames);
                var bodyPos = new List<Vector3>(frames);
                var bodyRot = new List<Quaternion>(frames);
                var hp = new HumanPose();
                string probeInfo;
                startedMode = !AnimationMode.InAnimationMode();
                if (startedMode) AnimationMode.StartAnimationMode();
                try
                {
                    // debug 探测：t = 0 / T/2 / T 采样后 sampleInst 的骨骼位移
                    //   （全为 0 ⇒ take 内容是静止姿势，或采样未生效）
                    var probes = new[] { 0f, src.length * 0.5f, src.length };
                    var probeParts = new string[probes.Length];
                    for (int p = 0; p < probes.Length; p++)
                    {
                        SampleOn(src, sampleInst, probes[p]);
                        var names = new List<string>();
                        CountDisplacement(sampleNodes, restRot, restPos, out int moved, out float maxAng, names);
                        probeParts[p] = $"t={probes[p]:F2}s→{moved}骨/{maxAng:F1}°[{string.Join(",", names)}]";
                    }
                    probeInfo = string.Join("，", probeParts);

                    using (var handler = new HumanPoseHandler(anim.avatar, extractInst.transform))
                    {
                        for (int f = 0; f < frames; f++)
                        {
                            float time = frame0 ? poseTime : Mathf.Min(f / fps, src.length);
                            SampleOn(src, sampleInst, time);
                            // TRS 逐帧拷贝到改名后的 extractInst，再提取 muscle
                            for (int i = 0; i < sampleNodes.Length; i++)
                            {
                                extractNodes[i].localPosition = sampleNodes[i].localPosition;
                                extractNodes[i].localRotation = sampleNodes[i].localRotation;
                                extractNodes[i].localScale = sampleNodes[i].localScale;
                            }
                            handler.GetHumanPose(ref hp);
                            times.Add(frame0 ? 0f : time);
                            var m = new float[HumanTrait.MuscleCount];
                            Array.Copy(hp.muscles, m, Mathf.Min(m.Length, hp.muscles.Length));
                            muscleVals.Add(m);
                            bodyPos.Add(hp.bodyPosition);
                            bodyRot.Add(hp.bodyRotation);
                        }
                    }
                }
                finally
                {
                    if (startedMode && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                    startedMode = false;
                }

                // 6) debug + sanity check：muscle 与 bodyPosition 全为 0 ⇒ 提取没生效
                float maxMuscle = 0f;
                if (muscleVals.Count > 0)
                    foreach (var v in muscleVals[0]) maxMuscle = Mathf.Max(maxMuscle, Mathf.Abs(v));
                bool any = maxMuscle > 1e-4f || (bodyPos.Count > 0 && bodyPos[0].sqrMagnitude > 1e-8f);
                if (!any)
                {
                    report = "HumanPoseHandler 提取失败（muscle 与 bodyPosition 全为 0，骨骼绑定未生效）。\n" +
                             "请确认导入页选择的 avatar 与当初导出 FBX 时使用的是同一个（同一套骨骼命名）。\n" +
                             $"debug：采样位移 {probeInfo}；max|muscle|={maxMuscle:F3}";
                    return null;
                }

                // 7) 写 muscle clip（与 Vpd2Anim 输出同格式）
                float outLen = frame0 ? 1f / 60f : src.length;
                var output = new AnimationClip { name = newName, frameRate = 60f };
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                {
                    var curve = frame0
                        ? AnimationCurve.Constant(0f, outLen, muscleVals[0][i])
                        : BuildCurve(times, f => muscleVals[f][i]);
                    output.SetCurve("", typeof(Animator), HumanTrait.MuscleName[i], curve);
                }
                output.SetCurve("", typeof(Animator), "RootT.x", RootCurve(times, f => bodyPos[f].x, frame0, outLen));
                output.SetCurve("", typeof(Animator), "RootT.y", RootCurve(times, f => bodyPos[f].y, frame0, outLen));
                output.SetCurve("", typeof(Animator), "RootT.z", RootCurve(times, f => bodyPos[f].z, frame0, outLen));
                output.SetCurve("", typeof(Animator), "RootQ.x", RootCurve(times, f => bodyRot[f].x, frame0, outLen));
                output.SetCurve("", typeof(Animator), "RootQ.y", RootCurve(times, f => bodyRot[f].y, frame0, outLen));
                output.SetCurve("", typeof(Animator), "RootQ.z", RootCurve(times, f => bodyRot[f].z, frame0, outLen));
                output.SetCurve("", typeof(Animator), "RootQ.w", RootCurve(times, f => bodyRot[f].w, frame0, outLen));

                var st = AnimationUtility.GetAnimationClipSettings(output);
                st.loopTime = false; st.startTime = 0f; st.stopTime = outLen;
                AnimationUtility.SetAnimationClipSettings(output, st);

                int curveCount = AnimationUtility.GetCurveBindings(src).Length;
                report = $"采样到 FBX 自身骨架（{curveCount} 条曲线）" +
                         (renamed > 0 ? $"；规范化改名 {renamed} 根骨骼" : "") +
                         $"；humanoid 骨骼匹配 {humanFound}/{humanTotal}" +
                         (scaled ? $"；单位比例 ×{ratio:G4}" : "") +
                         (poseNote != null ? "\n" + poseNote : "") +
                         $"\ndebug：采样位移 {probeInfo}；frame0 max|muscle|={maxMuscle:F3}，bodyPos={bodyPos[0]}";
                return output;
            }
            finally
            {
                if (startedMode && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                if (sampleInst != null) Object.DestroyImmediate(sampleInst);
                if (extractInst != null) Object.DestroyImmediate(extractInst);
            }
        }

        /// <summary>AnimationMode 采样（编辑器预览引擎，正确处理 legacy/非 legacy、四元数/欧拉角）。</summary>
        static void SampleOn(AnimationClip clip, GameObject go, float time)
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(go, clip, time);
            AnimationMode.EndSampling();
        }

        /// <summary>debug：统计采样后相对静止姿势发生位移的骨骼数与最大旋转角（names 记录前几个动了的骨骼名）。</summary>
        static void CountDisplacement(Transform[] nodes, Quaternion[] restRot, Vector3[] restPos,
            out int moved, out float maxAng, List<string> names)
        {
            moved = 0;
            maxAng = 0f;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i] == null) continue;
                float ang = Quaternion.Angle(restRot[i], nodes[i].localRotation);
                float dp = (restPos[i] - nodes[i].localPosition).magnitude;
                if (ang > 0.5f || dp > 1e-4f)
                {
                    moved++;
                    if (names != null && names.Count < 6) names.Add($"{nodes[i].name}({ang:F0}°)");
                }
                if (ang > maxAng) maxAng = ang;
            }
        }

        /// <summary>诊断：take 在指定时刻相对 FBX 默认姿势摆动 >2° 的骨骼数（纯曲线求值，支持四元数与欧拉角曲线）。</summary>
        static int CountDeviatedBonesAt(AnimationClip clip, Transform fbxRoot, float time)
        {
            var quats = new Dictionary<Transform, Quaternion>();
            var eulers = new Dictionary<Transform, Vector3>();
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                if (b.type != typeof(Transform)) continue;
                var c = AnimationUtility.GetEditorCurve(clip, b);
                if (c == null) continue;
                var t = b.path.Length == 0 ? fbxRoot : fbxRoot.Find(b.path);
                if (t == null) continue;
                float v = c.Evaluate(time);
                switch (b.propertyName)
                {
                    case "m_LocalRotation.x": { var q = GetOrInit(quats, t, t.localRotation); q.x = v; quats[t] = q; break; }
                    case "m_LocalRotation.y": { var q = GetOrInit(quats, t, t.localRotation); q.y = v; quats[t] = q; break; }
                    case "m_LocalRotation.z": { var q = GetOrInit(quats, t, t.localRotation); q.z = v; quats[t] = q; break; }
                    case "m_LocalRotation.w": { var q = GetOrInit(quats, t, t.localRotation); q.w = v; quats[t] = q; break; }
                    case "m_LocalEulerAnglesRaw.x": { var e = GetOrInit(eulers, t, t.localEulerAngles); e.x = v; eulers[t] = e; break; }
                    case "m_LocalEulerAnglesRaw.y": { var e = GetOrInit(eulers, t, t.localEulerAngles); e.y = v; eulers[t] = e; break; }
                    case "m_LocalEulerAnglesRaw.z": { var e = GetOrInit(eulers, t, t.localEulerAngles); e.z = v; eulers[t] = e; break; }
                }
            }
            int n = 0;
            foreach (var kvp in quats)
                if (Quaternion.Angle(kvp.Value.normalized, kvp.Key.localRotation) > 2f) n++;
            foreach (var kvp in eulers)
                if (!quats.ContainsKey(kvp.Key) && Quaternion.Angle(Quaternion.Euler(kvp.Value), kvp.Key.localRotation) > 2f) n++;
            return n;
        }

        /// <summary>扫描整个 take（0/25%/50%/75%/100%）：返回 t=0 摆动数、最大摆动数及其时刻。
        /// Blender 的 Force Start/End Key 可能在 take 开头写入静止帧、姿势在末尾帧。</summary>
        static void ScanDeviation(AnimationClip clip, Transform fbxRoot, out int dev0, out int devMax, out float bestTime)
        {
            dev0 = 0;
            devMax = 0;
            bestTime = 0f;
            const int steps = 4;
            for (int s = 0; s <= steps; s++)
            {
                float t = clip.length * s / steps;
                int d = CountDeviatedBonesAt(clip, fbxRoot, t);
                if (s == 0) dev0 = d;
                if (d > devMax) { devMax = d; bestTime = t; }
            }
        }

        static TV GetOrInit<TK, TV>(Dictionary<TK, TV> dict, TK key, TV init)
        {
            if (!dict.TryGetValue(key, out var v)) v = init;
            return v;
        }

        /// <summary>把 FBX 骨架里「规范化后与 avatar 骨骼名相同」的节点改名为 avatar 的精确名
        /// （HumanPoseHandler 按名绑定需要精确名）。仅【规范化骨骼名匹配】开启时执行。</summary>
        int RenameBonesToAvatarNames(Transform fbxRoot)
        {
            if (!_normalizeBoneNames) return 0;
            var byNorm = new Dictionary<string, string>();   // 规范化名 → avatar 原始名（冲突置 null 禁用）
            foreach (var t in _avatar.GetComponentsInChildren<Transform>(true))
            {
                var n = NormalizeBoneName(t.name);
                if (!byNorm.TryGetValue(n, out var cur)) byNorm[n] = t.name;
                else if (cur != t.name) byNorm[n] = null;
            }
            int renamed = 0;
            foreach (var t in fbxRoot.GetComponentsInChildren<Transform>(true))
            {
                if (byNorm.TryGetValue(NormalizeBoneName(t.name), out var target)
                    && target != null && t.name != target)
                { t.name = target; renamed++; }
            }
            return renamed;
        }

        static AnimationCurve BuildCurve(List<float> times, Func<int, float> getter)
        {
            var curve = new AnimationCurve();
            for (int f = 0; f < times.Count; f++)
                curve.AddKey(new Keyframe(times[f], getter(f)));
            return curve;
        }

        AnimationCurve RootCurve(List<float> times, Func<int, float> getter, bool frame0, float outLen) =>
            frame0 ? AnimationCurve.Constant(0f, outLen, getter(0)) : BuildCurve(times, getter);

        // ------------------------------------------------------------------
        // 名称辅助
        // ------------------------------------------------------------------
        /// <summary>剥掉 Blender 重名后缀（".001" 等）。</summary>
        static string StripBlenderSuffix(string name)
        {
            if (name.Length > 4 && name[name.Length - 4] == '.'
                && char.IsDigit(name[name.Length - 3])
                && char.IsDigit(name[name.Length - 2])
                && char.IsDigit(name[name.Length - 1]))
                return name.Substring(0, name.Length - 4);
            return name;
        }

        /// <summary>骨骼名规范化：剥 Blender 重名后缀、'.' 与 '_' 视为相同、忽略大小写。
        /// FBX 往返会把骨骼名里的 '.' 系统性转成 '_'（thigh.L → thigh_L）。</summary>
        static string NormalizeBoneName(string name) =>
            StripBlenderSuffix(name).Replace('.', '_').ToLowerInvariant();

        /// <summary>按名查找：先精确；【规范化骨骼名匹配】开启时再按规范化名。</summary>
        Transform FindByNameLoose(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            if (!_normalizeBoneNames) return null;
            var norm = NormalizeBoneName(name);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (NormalizeBoneName(t.name) == norm) return t;
            return null;
        }
    }
}
