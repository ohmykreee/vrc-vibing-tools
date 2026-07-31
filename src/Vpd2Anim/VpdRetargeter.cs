using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VpdToAnim
{
    /// <summary>
    /// Wraps the target avatar (scene instance or a temporary prefab instance):
    /// resolves humanoid bones and captures the rest pose.
    /// </summary>
    public class AvatarRig : IDisposable
    {
        public GameObject Instance;
        public bool IsTempInstance;
        public Animator Anim;
        public Transform Root;
        public readonly Dictionary<HumanBodyBones, Transform> Bone = new Dictionary<HumanBodyBones, Transform>();
        public readonly Dictionary<Transform, HumanBodyBones> BoneOf = new Dictionary<Transform, HumanBodyBones>();
        public readonly List<Transform> All = new List<Transform>();           // depth-first order
        public readonly Dictionary<Transform, Quaternion> RestLocalRot = new Dictionary<Transform, Quaternion>();
        public readonly Dictionary<Transform, Vector3> RestLocalPos = new Dictionary<Transform, Vector3>();
        public readonly Dictionary<Transform, Quaternion> RestWorldRot = new Dictionary<Transform, Quaternion>();
        public readonly Dictionary<Transform, Vector3> RestWorldPos = new Dictionary<Transform, Vector3>();
        public readonly Dictionary<string, Transform> ByName = new Dictionary<string, Transform>();

        public static AvatarRig Create(GameObject source, bool needHumanoid, out string error)
        {
            error = null;
            if (source == null) { error = "未指定 avatar。"; return null; }

            GameObject inst;
            bool temp = false;
            if (PrefabUtility.IsPartOfPrefabAsset(source))
            {
                inst = (GameObject)PrefabUtility.InstantiatePrefab(source);
                if (inst == null) { error = "prefab 实例化失败。"; return null; }
                inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                inst.hideFlags = HideFlags.HideAndDontSave;
                temp = true;
            }
            else inst = source;

            var anim = inst.GetComponent<Animator>();
            if (anim == null) anim = inst.GetComponentInChildren<Animator>();
            if (anim == null)
            {
                if (temp) Object.DestroyImmediate(inst);
                error = "avatar 上没有 Animator 组件。";
                return null;
            }
            if (needHumanoid && !anim.isHuman)
            {
                if (temp) Object.DestroyImmediate(inst);
                error = "avatar 的 Rig 不是 Humanoid。请在模型导入设置 Rig → Animation Type → Humanoid，或改用 Generic transform 输出模式。";
                return null;
            }

            var rig = new AvatarRig { Instance = inst, IsTempInstance = temp, Anim = anim, Root = anim.transform };
            rig.Collect(rig.Root);
            foreach (HumanBodyBones hbb in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (hbb == HumanBodyBones.LastBone) continue;
                var t = anim.GetBoneTransform(hbb);
                if (t == null || rig.Bone.ContainsKey(hbb)) continue;
                rig.Bone[hbb] = t;
                rig.BoneOf[t] = hbb;
            }
            return rig;
        }

        void Collect(Transform t)
        {
            All.Add(t);
            RestLocalRot[t] = t.localRotation;
            RestLocalPos[t] = t.localPosition;
            RestWorldRot[t] = t.rotation;
            RestWorldPos[t] = t.position;
            if (!ByName.ContainsKey(t.name)) ByName.Add(t.name, t);
            foreach (Transform c in t) Collect(c);
        }

        public void RestoreRest()
        {
            foreach (var t in All)
            {
                if (t == null) continue;
                t.localRotation = RestLocalRot[t];
                t.localPosition = RestLocalPos[t];
            }
        }

        public void Dispose()
        {
            if (IsTempInstance && Instance != null) Object.DestroyImmediate(Instance);
            Instance = null;
        }
    }

    /// <summary>
    /// Core retarget: builds MMD world-space pose deltas from the VPD (using the
    /// reference skeleton hierarchy), converts them to Unity space, and applies
    /// them on top of the avatar rest pose with optional rest-direction alignment.
    /// Legs whose 足ＩＫ bone is posed in the VPD are solved with a two-bone IK pass.
    /// </summary>
    public class VpdRetargeter
    {
        public VpdPose Pose;
        public bool Mirror;
        public AlignMode Align = AlignMode.Arms;
        public bool Fingers = true;
        public bool Eyes;
        public float ManualScale;                 // 0 = automatic (hip-height ratio)

        public float UsedScale { get; private set; } = 1f;
        public Vector3 HipsOffsetUnity { get; private set; }

        class IkLeg
        {
            public bool Active;
            public HumanBodyBones Upper, Knee, Ankle;
            public Vector3 TargetLocal;      // Unity space, relative to the rig root
            public Quaternion AnkleDelta;    // Unity world-space delta from the 足ＩＫ bone
        }

        readonly Dictionary<string, Quaternion> _worldRot = new Dictionary<string, Quaternion>(); // MMD space
        readonly Dictionary<string, Vector3> _worldPos = new Dictionary<string, Vector3>();       // MMD space
        readonly Dictionary<HumanBodyBones, MmdDef> _resolved = new Dictionary<HumanBodyBones, MmdDef>();
        readonly Dictionary<HumanBodyBones, Quaternion> _alignCorr = new Dictionary<HumanBodyBones, Quaternion>();
        readonly IkLeg[] _ikLegs = new IkLeg[2];

        public readonly List<string> MappedOk = new List<string>();
        public readonly List<string> MappedMissing = new List<string>();
        public readonly List<string> IkNotes = new List<string>();
        public IReadOnlyDictionary<HumanBodyBones, MmdDef> Resolved => _resolved;

        // ------------------------------------------------------------------
        public void Prepare(AvatarRig rig)
        {
            _worldRot.Clear(); _worldPos.Clear(); _resolved.Clear(); _alignCorr.Clear();
            MappedOk.Clear(); MappedMissing.Clear(); IkNotes.Clear();

            // 1) accumulate MMD world transforms (parents precede children in Defs)
            foreach (var d in MmdSkeleton.Defs)
            {
                Quaternion pr = Quaternion.identity;
                Vector3 pp = Vector3.zero, restLocal = d.RestPos;
                if (d.ParentKey != null)
                {
                    pr = _worldRot[d.ParentKey];
                    pp = _worldPos[d.ParentKey];
                    restLocal = d.RestPos - MmdSkeleton.ByKey[d.ParentKey].RestPos;
                }
                var vb = Pose.Find(d.Key);
                var lq = vb != null ? vb.Rotation : Quaternion.identity;
                var lp = restLocal + (vb != null ? vb.Position : Vector3.zero);
                _worldRot[d.Key] = (pr * lq).normalized;
                _worldPos[d.Key] = pp + pr * lp;
            }

            // 2) resolve humanoid mapping against this avatar (with side swap for Mirror)
            foreach (var d in MmdSkeleton.Defs)
            {
                if (d.Bone == HumanBodyBones.LastBone) continue;
                if (d.IsFinger && !Fingers) continue;
                if (d.IsEye && !Eyes) continue;

                var def = d;
                if (Mirror && d.MirrorKey != null) def = MmdSkeleton.ByKey[d.MirrorKey];

                var candidates = new List<HumanBodyBones> { d.Bone };
                if (d.Fallbacks != null) candidates.AddRange(d.Fallbacks);
                HumanBodyBones chosen = HumanBodyBones.LastBone;
                foreach (var c in candidates)
                    if (rig.Bone.ContainsKey(c) && !_resolved.ContainsKey(c)) { chosen = c; break; }

                if (chosen == HumanBodyBones.LastBone)
                {
                    MappedMissing.Add($"{d.Key} → {d.Bone}（avatar 未映射）");
                    continue;
                }
                _resolved[chosen] = def;
                MappedOk.Add($"{d.Key} → {chosen}");
            }

            // 3) hip scale & offset
            var centerDef = MmdSkeleton.ByKey[MmdSkeleton.Center];
            float refHipsY = MmdSkeleton.ByKey[MmdSkeleton.LowerBody].RestPos.y;
            float avatarHipsY = rig.Bone.TryGetValue(HumanBodyBones.Hips, out var hipsT)
                ? hipsT.position.y - rig.Root.position.y : 0.9f;
            UsedScale = ManualScale > 0f ? ManualScale : (refHipsY > 1e-3f ? avatarHipsY / refHipsY : 0.08f);

            var mmdOffset = _worldPos[MmdSkeleton.Center] - centerDef.RestPos;
            var u = MmdConvert.Pos(mmdOffset) * UsedScale;
            if (Mirror) u.x = -u.x;
            HipsOffsetUnity = u;

            // 4) rest-direction alignment corrections (A-pose fix etc.)
            foreach (var kv in _resolved)
            {
                var hbb = kv.Key; var def = kv.Value;
                var corr = Quaternion.identity;
                bool allowed = Align == AlignMode.All ? def.Group >= 1
                             : Align == AlignMode.Arms ? def.Group == 1
                             : false;
                // Alignment describes avatar-side anatomy, so it must always use the
                // UN-swapped def (when Mirror swapped the def, MirrorKey points back
                // to the original). MMD rest poses are left/right symmetric, so the
                // un-swapped rest direction is already correct for the avatar side.
                var alignDef = (Mirror && def.MirrorKey != null) ? MmdSkeleton.ByKey[def.MirrorKey] : def;
                if (allowed && alignDef.DirChildKey != null && alignDef.DirChildBones != null)
                {
                    var childDef = MmdSkeleton.ByKey[alignDef.DirChildKey];
                    var mmdDir = MmdConvert.Pos(childDef.RestPos - alignDef.RestPos);

                    Transform childT = null;
                    foreach (var cb in alignDef.DirChildBones)
                        if (rig.Bone.TryGetValue(cb, out childT)) break;

                    var t = rig.Bone[hbb];
                    if (childT != null && mmdDir.sqrMagnitude > 1e-8f)
                    {
                        var avDir = rig.RestWorldPos[childT] - rig.RestWorldPos[t];
                        if (avDir.sqrMagnitude > 1e-8f)
                            corr = Quaternion.FromToRotation(avDir.normalized, mmdDir.normalized);
                    }
                }
                _alignCorr[hbb] = corr;
            }

            // 5) leg IK setup: a posed (non-identity) 足ＩＫ bone means the whole leg
            //    is IK-driven in MMD; its FK chain values are overridden by the solver.
            for (int s = 0; s < 2; s++)
            {
                bool leftSide = s == 0;
                var leg = new IkLeg
                {
                    Upper = leftSide ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg,
                    Knee  = leftSide ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg,
                    Ankle = leftSide ? HumanBodyBones.LeftFoot     : HumanBodyBones.RightFoot,
                };
                string srcL = (leftSide != Mirror) ? "左" : "右";   // mirror → opposite source side
                string ikKey = srcL + "足IK";
                var vb = Pose.Find(ikKey);
                bool hasBones = rig.Bone.ContainsKey(leg.Upper) && rig.Bone.ContainsKey(leg.Knee)
                                && rig.Bone.ContainsKey(leg.Ankle);
                if (vb != null && !vb.IsIdentity && hasBones)
                {
                    leg.Active = true;
                    var p = MmdConvert.Pos(_worldPos[ikKey]) * UsedScale;
                    if (Mirror) p.x = -p.x;
                    leg.TargetLocal = p;
                    var dq = MmdConvert.Rot(_worldRot[ikKey]);
                    if (Mirror) dq = MmdConvert.MirrorRot(dq);
                    leg.AnkleDelta = dq;
                    IkNotes.Add($"{(leftSide ? "左腿" : "右腿")} ← {vb.Name}（IK 驱动）");
                }
                _ikLegs[s] = leg;
            }
        }

        bool IkSuppressed(HumanBodyBones hbb)
        {
            foreach (var l in _ikLegs)
                if (l != null && l.Active && (hbb == l.Upper || hbb == l.Knee || hbb == l.Ankle))
                    return true;
            return false;
        }

        // ------------------------------------------------------------------
        /// <summary>Poses the rig skeleton. Returns hips transform (or null).</summary>
        public Transform Apply(AvatarRig rig)
        {
            var targetWorldRot = new Dictionary<Transform, Quaternion>();
            var targetWorldPos = new Dictionary<Transform, Vector3>();
            Transform hips = rig.Bone.TryGetValue(HumanBodyBones.Hips, out var h) ? h : null;
            var rootRestRot = rig.RestWorldRot[rig.Root];

            foreach (var t in rig.All)
            {
                if (t == rig.Root)
                {
                    targetWorldRot[t] = rig.RestWorldRot[t];
                    targetWorldPos[t] = rig.RestWorldPos[t];
                    continue;
                }
                var p = t.parent;
                var pw = targetWorldRot[p];
                var pp = targetWorldPos[p];

                Quaternion w; Vector3 pos;
                if (rig.BoneOf.TryGetValue(t, out var hbb) && _resolved.TryGetValue(hbb, out var def)
                    && !IkSuppressed(hbb))
                {
                    var delta = MmdConvert.Rot(_worldRot[def.Key]);      // MMD rest world rot == identity
                    if (Mirror) delta = MmdConvert.MirrorRot(delta);
                    w = delta * _alignCorr[hbb] * rig.RestWorldRot[t];
                    pos = pp + pw * rig.RestLocalPos[t];
                    if (hbb == HumanBodyBones.Hips) pos = rig.RestWorldPos[t] + rootRestRot * HipsOffsetUnity;
                }
                else
                {
                    w = pw * rig.RestLocalRot[t];
                    pos = pp + pw * rig.RestLocalPos[t];
                }
                targetWorldRot[t] = w;
                targetWorldPos[t] = pos;

                t.localRotation = Quaternion.Inverse(pw) * w;
                if (t == hips)
                    t.localPosition = Quaternion.Inverse(pw) * (pos - pp);
            }

            // two-bone IK pass for IK-driven legs (must run after the FK pass)
            foreach (var leg in _ikLegs)
                if (leg != null && leg.Active) SolveLegIk(rig, leg);

            return hips;
        }

        void SolveLegIk(AvatarRig rig, IkLeg leg)
        {
            var upper = rig.Bone[leg.Upper];
            var knee = rig.Bone[leg.Knee];
            var ankle = rig.Bone[leg.Ankle];

            Vector3 T = rig.RestWorldPos[rig.Root] + rig.RestWorldRot[rig.Root] * leg.TargetLocal;
            Vector3 H = upper.position;
            float l1 = Vector3.Distance(rig.RestWorldPos[upper], rig.RestWorldPos[knee]);
            float l2 = Vector3.Distance(rig.RestWorldPos[knee], rig.RestWorldPos[ankle]);
            if (l1 < 1e-6f || l2 < 1e-6f) return;
            float maxReach = (l1 + l2) * 0.99999f;
            var ht = T - H;
            if (ht.magnitude > maxReach) T = H + ht.normalized * maxReach;
            if (ht.sqrMagnitude < 1e-10f) return;

            // CCD: aim the whole leg from the hip, then flex the knee onto the target.
            for (int i = 0; i < 10; i++)
            {
                Vector3 A = ankle.position;
                if ((A - T).sqrMagnitude < 1e-10f) break;
                if ((A - H).sqrMagnitude > 1e-10f && (T - H).sqrMagnitude > 1e-10f)
                    SetWorldRot(upper, Quaternion.FromToRotation(A - H, T - H) * upper.rotation);
                Vector3 K = knee.position;
                A = ankle.position;
                if ((A - K).sqrMagnitude > 1e-10f && (T - K).sqrMagnitude > 1e-10f)
                    SetWorldRot(knee, Quaternion.FromToRotation(A - K, T - K) * knee.rotation);
            }

            // ankle orientation comes from the 足ＩＫ bone's world rotation
            SetWorldRot(ankle, leg.AnkleDelta * rig.RestWorldRot[ankle]);
        }

        static void SetWorldRot(Transform t, Quaternion worldRot)
        {
            t.localRotation = Quaternion.Inverse(t.parent.rotation) * worldRot;
        }

        // ------------------------------------------------------------------
        public string BuildReport(AvatarRig rig)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"模型：{Pose.ModelName}   骨骼：{Pose.Bones.Count}   有姿势：{Pose.PosedCount}");
            sb.AppendLine($"髋部缩放：{UsedScale:F4} 米/单位   髋部偏移（Unity）：{HipsOffsetUnity}");
            sb.AppendLine($"已映射（{MappedOk.Count}）：" + string.Join(", ", MappedOk));
            if (IkNotes.Count > 0)
                sb.AppendLine("IK：" + string.Join(", ", IkNotes));
            if (MappedMissing.Count > 0)
                sb.AppendLine("avatar 上未映射：" + string.Join(", ", MappedMissing));
            var unused = new List<string>();
            foreach (var b in Pose.Bones)
            {
                if (b.IsIdentity) continue;
                var key = MmdSkeleton.Normalize(b.Name);
                if (!MmdSkeleton.ByKey.ContainsKey(key)) unused.Add(b.Name);
            }
            if (unused.Count > 0)
                sb.AppendLine("有姿势但非 humanoid（头发/裙子/附件 IK 等，已忽略）：" + string.Join(", ", unused));
            return sb.ToString();
        }
    }

    /// <summary>Builds the final AnimationClip assets from a posed rig.</summary>
    public static class AnimClipBuilder
    {
        /// <summary>Clip length: exactly one frame at 60 fps — a single static pose.</summary>
        const float Duration = 1f / 60f;

        static AnimationCurve Const(float v) => AnimationCurve.Constant(0f, Duration, v);

        /// <summary>Humanoid muscle clip – retargets to ANY humanoid VRChat avatar.</summary>
        public static AnimationClip BuildMuscleClip(AvatarRig rig, string name)
        {
            // Unity 2021.2+ : GetHumanPose takes 'ref' (was 'out' before).
            var hp = new HumanPose();
            using (var handler = new HumanPoseHandler(rig.Anim.avatar, rig.Root))
                handler.GetHumanPose(ref hp);

            var clip = new AnimationClip { name = name, frameRate = 60f };
            var muscles = hp.muscles;
            int count = Mathf.Min(muscles.Length, HumanTrait.MuscleCount);
            for (int i = 0; i < count; i++)
                clip.SetCurve("", typeof(Animator), HumanTrait.MuscleName[i], Const(muscles[i]));

            // bodyPosition is normalized by avatar human scale – exactly what RootT expects.
            SetConst(clip, "RootT.x", hp.bodyPosition.x);
            SetConst(clip, "RootT.y", hp.bodyPosition.y);
            SetConst(clip, "RootT.z", hp.bodyPosition.z);
            SetConst(clip, "RootQ.x", hp.bodyRotation.x);
            SetConst(clip, "RootQ.y", hp.bodyRotation.y);
            SetConst(clip, "RootQ.z", hp.bodyRotation.z);
            SetConst(clip, "RootQ.w", hp.bodyRotation.w);
            return clip;
        }

        /// <summary>Generic transform-curve clip (relative paths from the avatar root).</summary>
        public static AnimationClip BuildGenericClip(AvatarRig rig, VpdRetargeter rt, string name)
        {
            var clip = new AnimationClip { name = name, frameRate = 60f };
            foreach (var hbb in rt.Resolved.Keys)
            {
                if (!rig.Bone.TryGetValue(hbb, out var t)) continue;
                WriteTransformCurves(clip, rig.Root, t, hbb == HumanBodyBones.Hips, true);
            }
            return clip;
        }

        public static void WriteTransformCurves(AnimationClip clip, Transform root, Transform t,
            bool position, bool rotation)
        {
            var path = AnimationUtility.CalculateTransformPath(t, root);
            if (rotation)
            {
                var q = t.localRotation.normalized;
                SetEditorConst(clip, path, "m_LocalRotation.x", q.x);
                SetEditorConst(clip, path, "m_LocalRotation.y", q.y);
                SetEditorConst(clip, path, "m_LocalRotation.z", q.z);
                SetEditorConst(clip, path, "m_LocalRotation.w", q.w);
            }
            if (position)
            {
                SetEditorConst(clip, path, "m_LocalPosition.x", t.localPosition.x);
                SetEditorConst(clip, path, "m_LocalPosition.y", t.localPosition.y);
                SetEditorConst(clip, path, "m_LocalPosition.z", t.localPosition.z);
            }
        }

        /// <summary>
        /// Extra (non-humanoid) bones: if the avatar has a transform whose name exactly
        /// matches a VPD bone (hair/skirt/sleeves of MMD-derived avatars), write its
        /// converted local rotation as a transform curve.
        /// </summary>
        public static int WriteExtraBoneCurves(AnimationClip clip, AvatarRig rig, VpdPose pose, bool mirror)
        {
            int written = 0;
            foreach (var b in pose.Bones)
            {
                var key = MmdSkeleton.Normalize(b.Name);
                if (MmdSkeleton.ByKey.ContainsKey(key)) continue;       // known humanoid/chain bones
                if (!rig.ByName.TryGetValue(b.Name, out var t)) continue;

                var q = MmdConvert.Rot(b.Rotation);
                if (mirror) q = MmdConvert.MirrorRot(q);
                t.localRotation = q;
                WriteTransformCurves(clip, rig.Root, t, false, true);
                written++;
            }
            return written;
        }

        static void SetConst(AnimationClip clip, string prop, float v) =>
            clip.SetCurve("", typeof(Animator), prop, Const(v));

        static void SetEditorConst(AnimationClip clip, string path, string prop, float v) =>
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(path, typeof(Transform), prop),
                Const(v));
    }
}
