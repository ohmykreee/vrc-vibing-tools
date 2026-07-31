using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VpdToAnim
{
    /// <summary>One bone entry of a VPD file (raw MMD-space values).</summary>
    public class VpdBone
    {
        public string Name;
        public Vector3 Position;      // MMD units, MMD coordinate space, offset from rest
        public Quaternion Rotation;   // MMD-space local quaternion (x,y,z,w), normalized

        public bool IsIdentity
        {
            get
            {
                if (Position.sqrMagnitude > 1e-8f) return false;
                var q = Rotation.normalized;
                return Mathf.Abs(Mathf.Abs(q.w) - 1f) < 1e-4f
                       && Mathf.Abs(q.x) < 1e-4f && Mathf.Abs(q.y) < 1e-4f && Mathf.Abs(q.z) < 1e-4f;
            }
        }
    }

    /// <summary>
    /// Parser for MMD "Vocaloid Pose Data" (.vpd) files.
    ///
    /// Format (plain text, Shift-JIS / CP932 encoding):
    ///   Vocaloid Pose Data file
    ///   &lt;model file name&gt;;        // parent file name
    ///   &lt;bone count&gt;;             // total pose bones
    ///   Bone0{ボーン名
    ///     x,y,z;                      // translation offset from rest pose
    ///     x,y,z,w;                    // local rotation quaternion
    ///   }
    ///   ... (an optional Morph section after the bones is ignored)
    ///
    /// Semantics: values are LOCAL to each bone and relative to the model's rest
    /// pose (the pose the model was rigged in). A VPD therefore only makes sense
    /// together with a skeleton definition — see MmdSkeleton.
    /// </summary>
    public class VpdPose
    {
        public string ModelName = "";
        public readonly List<VpdBone> Bones = new List<VpdBone>();
        readonly Dictionary<string, VpdBone> _byNormalized = new Dictionary<string, VpdBone>();

        /// <summary>Find a bone by its normalized MMD name (see MmdSkeleton.Normalize).</summary>
        public VpdBone Find(string normalizedName)
        {
            _byNormalized.TryGetValue(normalizedName, out var b);
            return b;
        }

        public int PosedCount
        {
            get { int n = 0; foreach (var b in Bones) if (!b.IsIdentity) n++; return n; }
        }

        // ------------------------------------------------------------------
        // Decoding (VPD files are Shift-JIS / CP932)
        // ------------------------------------------------------------------
        public static string Decode(byte[] data)
        {
            // .NET Standard/Core needs the code-pages provider for shift_jis;
            // Unity/Mono resolves it via I18N.CJK.dll. Try both, never throw.
            try
            {
                var t = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                var inst = t?.GetProperty("Instance")?.GetValue(null, null) as EncodingProvider;
                if (inst != null) Encoding.RegisterProvider(inst);
            }
            catch { /* ignored */ }

            foreach (var name in new[] { "shift_jis", "cp932" })
            {
                try { return Encoding.GetEncoding(name).GetString(data); }
                catch { /* try next */ }
            }
            try { return new UTF8Encoding(false, true).GetString(data); }
            catch { return Encoding.UTF8.GetString(data); }
        }

        // ------------------------------------------------------------------
        // Parsing
        // ------------------------------------------------------------------
        static readonly Regex BoneBlock = new Regex(
            @"Bone\d+\s*\{\s*([^\r\n]+?)\s*\r?\n\s*([^;\r\n]+);[^\r\n]*\r?\n\s*([^;\r\n]+);",
            RegexOptions.Compiled);

        public static VpdPose Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new Exception("VPD 内容为空");
            if (!text.Contains("Vocaloid Pose Data file"))
                Debug.LogWarning("[VpdToAnim] 未找到文件头 'Vocaloid Pose Data file'——仍将尝试解析。");

            var pose = new VpdPose();

            // Model name: first non-empty line after the header, before ';'
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0) continue;
                int semi = l.IndexOf(';');
                pose.ModelName = (semi >= 0 ? l.Substring(0, semi) : l).Trim();
                break;
            }

            foreach (Match m in BoneBlock.Matches(text))
            {
                var pos = ParseFloats(m.Groups[2].Value);
                var rot = ParseFloats(m.Groups[3].Value);
                if (pos.Length < 3 || rot.Length < 4) continue;
                var b = new VpdBone
                {
                    Name = m.Groups[1].Value.Trim(),
                    Position = new Vector3(pos[0], pos[1], pos[2]),
                    Rotation = new Quaternion(rot[0], rot[1], rot[2], rot[3]).normalized
                };
                pose.Bones.Add(b);
                pose._byNormalized[MmdSkeleton.Normalize(b.Name)] = b;
            }

            if (pose.Bones.Count == 0)
                throw new Exception("未找到 'Bone{n}' 骨骼块——这真的是 .vpd 文件吗？");
            return pose;
        }

        static float[] ParseFloats(string s)
        {
            var parts = s.Split(',');
            var r = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]);
            return r;
        }
    }
}
