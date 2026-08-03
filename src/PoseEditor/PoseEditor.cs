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
    ///      姿势烘焙成单帧动画随 FBX 导出。
    ///
    /// 【制作姿势流程】
    ///   纯说明页签：Blender 中只保留 body mesh/armature/Animation，Pose Mode 下
    ///   在第 1 帧摆姿，按 A 全选骨骼 → 右键 Insert Keyframe with Keying Set →
    ///   Location、Rotation and Scale → 导出 FBX；回 Unity 后在 Rig 界面改 Humanoid，
    ///   展开 FBX 找到 anim 子文件 Ctrl+D 复制；仅第 1 帧姿势时用 PoseBankBuilder 提取。
    /// </summary>
    public class PoseEditor : EditorWindow
    {
        const string FbxExporterTypeName = "UnityEditor.Formats.Fbx.Exporter.ModelExporter";
        const string FbxSettingsTypeName = "UnityEditor.Formats.Fbx.Exporter.ExportModelSettingsSerialize";

        [MenuItem("Tools/Vibing Tools/Pose Exporter to FBX", priority = 40)]
        static void Open() => GetWindow<PoseEditor>("Pose Editor");

        // ---- 共享 ----
        [SerializeField] GameObject _avatar;                  // 两个页签共用（场景对象或 prefab 资产）

        // ---- 导出 ----
        [SerializeField] AnimationClip _clip;
        [SerializeField] SkinnedMeshRenderer _bodyMesh;       // 导出内容：body mesh
        [SerializeField] Transform _skeletonRoot;             // 导出内容：骨架根

        [SerializeField] bool[] _folds = new bool[2];
        [SerializeField] string _log =
            "【导出 FBX】选 avatar + pose .anim → 自动检测 body mesh/骨架 → 导出 FBX → 在 Blender 中制作姿势（见「制作姿势流程」页签）。";

        int _tab;
        Vector2 _scroll, _helpScroll, _workScroll;
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
            _tab = GUILayout.Toolbar(_tab, new[] { "导出 FBX（去 Blender）", "制作姿势流程", "使用说明" });
            EditorGUILayout.Space();

            switch (_tab)
            {
                case 0: DrawExportTab(); break;
                case 1: DrawWorkflowTab(); break;
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
        // 页签 2：制作姿势流程（纯说明，无逻辑）
        // ------------------------------------------------------------------
        void DrawWorkflowTab()
        {
            _workScroll = EditorGUILayout.BeginScrollView(_workScroll);
            EditorGUILayout.LabelField("【在 Blender 中制作姿势】", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "1. 导入文件后，只保留 Body、Armature、Animation，其余物体删除。\n" +
                "2. 点击角色，进入 Pose Mode。\n" +
                "3. 选中第一帧。\n" +
                "4. 修改骨骼位置或旋转。\n" +
                "5. 按 A 选中所有骨骼。\n" +
                "6. 右键呼出菜单，选择 Insert Keyframe with Keying Set。\n" +
                "7. 然后选择 Location、Rotation & Scale。\n" +
                "8. 导出 FBX。",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("【在 Unity 中处理】", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "1. 把 FBX 拖入 Unity。\n" +
                "2. 在 FBX 的 Inspector 窗口的 Rig 界面改为 Humanoid。\n" +
                "3. 在资源管理器（Project 窗口）展开 FBX，找到 anim 子文件，Ctrl+D 复制出文件。\n" +
                "4. 如果想要的姿势只在第 1 帧，可以使用 PoseBankBuilder 处理提取出来。",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
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
            HelpFold(0, "导出页说明 / 自动检测",
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
            HelpFold(1, "依赖与注意事项",
                "● 依赖 Unity 官方 FBX Exporter（com.unity.formats.fbx）：\n" +
                "  未安装时打开本工具会弹窗引导安装并退出。\n" +
                "● 从 FBX 复制出的 anim 只记录 humanoid muscle 骨骼；头发/衣服/附件等\n" +
                "  非 humanoid 骨骼的姿势不会被保留。\n" +
                "● avatar 的导入设置必须是 Rig → Animation Type → Humanoid。\n" +
                "● 若姿势不对：检查 Blender 侧是否改过骨骼名、动过 Armature 物体，\n" +
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
                       "下一步：按「制作姿势流程」页签的步骤，在 Blender 中导入该 FBX 修改姿势。";
                EditorUtility.DisplayDialog("Pose Editor — 导出完成",
                    "FBX 已导出：\n" + exported + "\n\n" +
                    "下一步请按「制作姿势流程」页签的步骤，在 Blender 中制作姿势并回导到 Unity。", "好");
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


    }
}
