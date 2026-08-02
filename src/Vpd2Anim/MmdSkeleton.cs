using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VpdToAnim
{
    public enum AlignMode
    {
        None = 0,   // pure world-delta retarget (avatar rest pose is treated as MMD rest pose)
        Arms = 1,   // additionally align arm chains (fixes MMD A-pose vs. VRChat T-pose)
        All  = 2,   // align arms + legs + spine chains
    }

    /// <summary>Which IK-driven legs get the muscle-range twist correction.</summary>
    public enum TwistCorrectMode
    {
        None      = 0,   // no correction: raw IK solve result
        LeftOnly  = 1,   // correct the left leg only
        RightOnly = 2,   // correct the right leg only
        Both      = 3,   // correct both legs
    }

    /// <summary>
    /// MMD → Unity coordinate conversion.
    ///
    /// MMD:   left-handed, Y-up, model faces -Z, anatomical LEFT = +X.
    /// Unity: left-handed, Y-up, humanoid faces +Z, anatomical LEFT = -X.
    /// Therefore the correct change of basis is a proper 180° rotation about Y
    /// (NOT a mirror):  pos (-x, y, -z),  quat (-qx, qy, -qz, qw).
    /// Left stays Left, Right stays Right. This matches what mmd-for-unity /
    /// MMD4Mecanim effectively do (X-flip import + 180° turn at the センター bone).
    /// </summary>
    public static class MmdConvert
    {
        public static Vector3 Pos(Vector3 p) => new Vector3(-p.x, p.y, -p.z);
        public static Quaternion Rot(Quaternion q) => new Quaternion(-q.x, q.y, -q.z, q.w);

        // Optional whole-pose mirror (left/right swap), applied in UNITY space.
        public static Vector3 MirrorPos(Vector3 p) => new Vector3(-p.x, p.y, p.z);
        public static Quaternion MirrorRot(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);
    }

    /// <summary>Definition of one standard MMD bone: humanoid mapping + reference rest data.</summary>
    public class MmdDef
    {
        public string Key;                                    // normalized MMD name (half-width digits)
        public string ParentKey;                              // reference-skeleton parent key
        public Vector3 RestPos;                               // MMD world-space rest position (MMD units)
        public HumanBodyBones Bone = HumanBodyBones.LastBone; // LastBone = chain-only bone
        public HumanBodyBones[] Fallbacks;                    // tried in order if Bone missing on avatar
        public string DirChildKey;                            // child used for rest-direction alignment
        public HumanBodyBones[] DirChildBones;                // avatar-side equivalent child candidates
        public int Group;                                     // 0 none, 1 arm, 2 leg, 3 spine (alignment groups)
        public bool IsFinger;
        public bool IsEye;
        public int Side;                                      // -1 right, 0 center, +1 left (MMD: left = +X)
        public string MirrorKey;                              // key of the opposite-side twin
    }

    /// <summary>
    /// Standard MMD skeleton (Miku/Tda-style proportions, MMD units).
    /// Rest pose has all local rotations = identity, so bone *world* rest rotations
    /// are identity too — that is what makes pure-delta retargeting from VPD possible.
    /// Positions are only used for (a) hierarchy directions (A-pose alignment),
    /// (b) hip-height scale estimation and (c) IK target accumulation, so
    /// approximate values are fine.
    /// </summary>
    public static class MmdSkeleton
    {
        public const string OperationCenter = "操作中心";
        public const string Root = "全ての親";
        public const string Center = "センター";
        public const string LowerBody = "下半身";

        public static readonly List<MmdDef> Defs = new List<MmdDef>();
        public static readonly Dictionary<string, MmdDef> ByKey = new Dictionary<string, MmdDef>();

        static MmdDef Add(string key, string parent, Vector3 rest,
            HumanBodyBones bone = HumanBodyBones.LastBone, HumanBodyBones[] fallbacks = null,
            string dirChild = null, HumanBodyBones[] dirChildBones = null,
            int group = 0, bool finger = false, bool eye = false, int side = 0)
        {
            var d = new MmdDef
            {
                Key = key, ParentKey = parent, RestPos = rest, Bone = bone, Fallbacks = fallbacks,
                DirChildKey = dirChild, DirChildBones = dirChildBones,
                Group = group, IsFinger = finger, IsEye = eye, Side = side
            };
            Defs.Add(d);
            ByKey[key] = d;
            return d;
        }

        static MmdSkeleton()
        {
            // ---- center chain ----
            // 操作中心 (operation center) is the true root of Tda-style models; some
            // models don't have it — harmless then, because missing bones are identity.
            Add(OperationCenter, null, new Vector3(0, 0, 0));
            Add(Root, OperationCenter, new Vector3(0, 0, 0));
            Add(Center, Root, new Vector3(0, 10.0f, 0), HumanBodyBones.Hips);
            Add("グルーブ", Center, new Vector3(0, 10.0f, 0));
            Add("腰", "グルーブ", new Vector3(0, 10.0f, 0));
            Add("上半身", "腰", new Vector3(0, 12.4f, 0), HumanBodyBones.Spine,
                new[] { HumanBodyBones.Chest }, "上半身2", new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck }, 3);
            Add("上半身2", "上半身", new Vector3(0, 13.8f, 0), HumanBodyBones.UpperChest,
                new[] { HumanBodyBones.Chest }, "首", new[] { HumanBodyBones.Neck, HumanBodyBones.Head }, 3);
            Add("上半身3", "上半身2", new Vector3(0, 14.4f, 0), HumanBodyBones.UpperChest,
                null, "首", new[] { HumanBodyBones.Neck, HumanBodyBones.Head }, 3);
            Add("首", "上半身2", new Vector3(0, 15.4f, 0), HumanBodyBones.Neck,
                new[] { HumanBodyBones.Head }, "頭", new[] { HumanBodyBones.Head }, 3);
            Add("頭", "首", new Vector3(0, 16.3f, 0), HumanBodyBones.Head, null, null, null, 3);
            Add(LowerBody, Center, new Vector3(0, 9.8f, 0));

            // ---- limbs, both sides (MMD: 左 = +X, 右 = -X) ----
            for (int s = 0; s < 2; s++)
            {
                int side = s == 0 ? 1 : -1;                 // +1 = left
                string L = side > 0 ? "左" : "右";
                float x = side > 0 ? 1f : -1f;              // MMD x sign
                bool left = side > 0;

                var shoulder = left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder;
                var upperArm = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
                var lowerArm = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
                var hand     = left ? HumanBodyBones.LeftHand     : HumanBodyBones.RightHand;
                var upperLeg = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
                var lowerLeg = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
                var foot     = left ? HumanBodyBones.LeftFoot     : HumanBodyBones.RightFoot;
                var toes     = left ? HumanBodyBones.LeftToes     : HumanBodyBones.RightToes;
                var eyeB     = left ? HumanBodyBones.LeftEye      : HumanBodyBones.RightEye;
                var midProx  = left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal;

                Add(L + "目", "頭", new Vector3(0.35f * x, 16.9f, 0.55f), eyeB, null, null, null, 0, false, true, side);

                Add(L + "肩", "上半身2", new Vector3(0.65f * x, 13.9f, 0), shoulder,
                    null, L + "腕", new[] { upperArm }, 1, false, false, side);
                Add(L + "腕", L + "肩", new Vector3(2.00f * x, 13.45f, 0), upperArm,
                    null, L + "ひじ", new[] { lowerArm }, 1, false, false, side);
                Add(L + "ひじ", L + "腕", new Vector3(4.25f * x, 11.25f, 0), lowerArm,
                    null, L + "手首", new[] { hand }, 1, false, false, side);
                Add(L + "手首", L + "ひじ", new Vector3(6.30f * x, 9.35f, 0), hand,
                    null, L + "中指1", new[] { midProx }, 1, false, false, side);

                // fingers (parents only matter for world-delta accumulation)
                AddFinger(L, "親指", new[] { "0", "1", "2" }, x, side,
                    new Vector3(6.55f, 9.00f, 0.30f), new Vector3(0.45f, -0.45f, 0.15f),
                    left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal,
                    left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate,
                    left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal);
                AddFinger(L, "人指", new[] { "1", "2", "3" }, x, side,
                    new Vector3(7.05f, 8.90f, 0.15f), new Vector3(0.70f, -0.85f, 0.05f),
                    left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal,
                    left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate,
                    left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
                AddFinger(L, "中指", new[] { "1", "2", "3" }, x, side,
                    new Vector3(7.10f, 8.95f, 0.00f), new Vector3(0.75f, -0.85f, 0.00f),
                    left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal,
                    left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate,
                    left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
                AddFinger(L, "薬指", new[] { "1", "2", "3" }, x, side,
                    new Vector3(7.05f, 8.85f, -0.15f), new Vector3(0.75f, -0.85f, -0.05f),
                    left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal,
                    left ? HumanBodyBones.LeftRingIntermediate : HumanBodyBones.RightRingIntermediate,
                    left ? HumanBodyBones.LeftRingDistal : HumanBodyBones.RightRingDistal);
                AddFinger(L, "小指", new[] { "1", "2", "3" }, x, side,
                    new Vector3(6.95f, 8.70f, -0.30f), new Vector3(0.60f, -0.75f, -0.10f),
                    left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal,
                    left ? HumanBodyBones.LeftLittleIntermediate : HumanBodyBones.RightLittleIntermediate,
                    left ? HumanBodyBones.LeftLittleDistal : HumanBodyBones.RightLittleDistal);

                Add(L + "足", LowerBody, new Vector3(0.90f * x, 9.30f, 0), upperLeg,
                    null, L + "ひざ", new[] { lowerLeg }, 2, false, false, side);
                Add(L + "ひざ", L + "足", new Vector3(0.90f * x, 4.90f, 0), lowerLeg,
                    null, L + "足首", new[] { foot }, 2, false, false, side);
                Add(L + "足首", L + "ひざ", new Vector3(0.90f * x, 1.10f, 0), foot,
                    null, L + "つま先", new[] { toes }, 2, false, false, side);
                Add(L + "つま先", L + "足首", new Vector3(0.90f * x, 0.30f, 1.30f), toes,
                    null, null, null, 0, false, false, side);

                // Leg IK chain (足ＩＫ). Very common in VPD poses: the FK bones of the
                // leg stay identity and the ankle target is given by the IK bone instead.
                // Chain-only defs (no humanoid bone) — consumed by VpdRetargeter's IK pass.
                // Both rest at the ankle position; the intermediate 足IK親 keeps pose
                // values of either parenting style (with/without 足IK親) equivalent.
                Add(L + "足IK親", Root, new Vector3(0.90f * x, 1.10f, 0), HumanBodyBones.LastBone,
                    null, null, null, 0, false, false, side);
                Add(L + "足IK", L + "足IK親", new Vector3(0.90f * x, 1.10f, 0), HumanBodyBones.LastBone,
                    null, null, null, 0, false, false, side);
            }

            // mirror twins
            foreach (var d in Defs)
            {
                if (d.Side == 0) continue;
                var mk = d.Key.Replace(d.Side > 0 ? "左" : "右", d.Side > 0 ? "右" : "左");
                if (ByKey.ContainsKey(mk)) d.MirrorKey = mk;
            }
        }

        static void AddFinger(string L, string finger, string[] suffixes, float x, int side,
            Vector3 firstPos, Vector3 step,
            HumanBodyBones b0, HumanBodyBones b1, HumanBodyBones b2)
        {
            var bones = new[] { b0, b1, b2 };
            string parent = L + "手首";
            for (int i = 0; i < suffixes.Length; i++)
            {
                string key = L + finger + suffixes[i];
                Vector3 pos = firstPos + step * i;
                pos.x *= x;
                Add(key, parent, new Vector3(pos.x, pos.y, pos.z), bones[i],
                    null, null, null, 0, true, false, side);
                parent = key;
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Full-width digits/letters → ASCII, trim, common aliases.</summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.Trim())
            {
                if (c >= '０' && c <= '９') sb.Append((char)(c - '０' + '0'));
                else if (c >= 'Ａ' && c <= 'Ｚ') sb.Append((char)(c - 'Ａ' + 'A'));
                else if (c >= 'ａ' && c <= 'ｚ') sb.Append((char)(c - 'ａ' + 'a'));
                else sb.Append(c);
            }
            var s = sb.ToString().Replace(" ", "").Replace("　", "");
            s = s.Replace("人差指", "人指").Replace("肘", "ひじ");
            return s;
        }
    }
}
