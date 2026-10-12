using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Iced.Intel;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

// Dev-only reverse engineering aid. Not part of the mod.
//  - disassembles the methods listed in BepInEx\probe\targets.txt into BepInEx\probe\asm\
//  - writes field offsets of the listed classes to BepInEx\probe\fields.txt
//  - once the game database has loaded, dumps lure/fish/config data to BepInEx\probe\data.txt
namespace CncProbe
{
    [BepInPlugin("mike.castnchill.probe", "CnC Probe", "0.1.0")]
    public unsafe class ProbePlugin : BasePlugin
    {
        internal static ProbePlugin Instance;
        internal static string OutDir;

        public override void Load()
        {
            Instance = this;
            OutDir = Path.Combine(Paths.BepInExRootPath, "probe");
            Directory.CreateDirectory(OutDir);
            try { new Disassembly(Log).Run(); }
            catch (Exception e) { Log.LogError("Disassembly failed: " + e); }
            try { AddComponent<ProbeBehaviour>(); }
            catch (Exception e) { Log.LogError("AddComponent failed: " + e); }
        }
    }

    public class ProbeBehaviour : MonoBehaviour
    {
        public ProbeBehaviour(IntPtr ptr) : base(ptr) { }

        float m_nextTry = 1f;
        bool m_done;

        void Update()
        {
            if (m_done || Time.realtimeSinceStartup < m_nextTry) return;
            m_nextTry = Time.realtimeSinceStartup + 1f;
            try
            {
                var lures = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Game.LureData>());
                var fish = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Game.FishData>());
                if (lures.Length == 0 || fish.Length == 0) return;
                m_done = true;
                DataDump.Run();
                ProbePlugin.Instance.Log.LogMessage("Probe data dump complete");
            }
            catch (Exception e)
            {
                m_done = true;
                ProbePlugin.Instance.Log.LogError("Data dump failed: " + e);
            }
        }
    }

    static class DataDump
    {
        static StringBuilder sb;

        public static void Run()
        {
            sb = new StringBuilder();
            DumpAll<Game.LureConfig>("LureConfig");
            DumpAll<Game.FishingLineConfig>("FishingLineConfig");
            DumpAll<Game.GameplayConfig>("GameplayConfig");
            DumpAll<Game.FishConfig>("FishConfig");
            DumpAll<Game.PhysicsConfig>("PhysicsConfig");
            DumpAll<Game.IdleConfig>("IdleConfig");
            DumpAll<Game.TechniqueData>("TechniqueData");
            DumpAll<Game.LureTypeData>("LureTypeData");
            DumpAll<Game.LineTypeData>("LineTypeData");
            DumpAll<Game.LureData>("LureData");
            DumpAll<Game.RodData>("RodData");
            DumpAll<Game.FishSizeData>("FishSizeData");
            DumpAll<Game.FishChaseActionData>("FishChaseActionData");
            DumpAll<Game.FishIdleActionData>("FishIdleActionData");
            DumpAll<Game.FishFightActionData>("FishFightActionData");
            DumpAll<Game.RodStrengthData>("RodStrengthData");
            DumpAll<Game.FishData>("FishData");
            File.WriteAllText(Path.Combine(ProbePlugin.OutDir, "data.txt"), sb.ToString());
        }

        static void DumpAll<T>(string title) where T : UnityEngine.Object
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
            sb.AppendLine("==== " + title + " x" + all.Length + " ====");
            var items = new List<T>();
            foreach (var o in all) { var t = o.TryCast<T>(); if (t != null) items.Add(t); }
            foreach (var item in items.OrderBy(Order))
            {
                sb.AppendLine("-- " + Describe(item));
                DumpFields(item, typeof(T), "   ", 0);
            }
        }

        static int Order(UnityEngine.Object o)
        {
            var od = o.TryCast<Game.ObjectData>();
            return od != null ? od.order : 0;
        }

        static string RuntimeClass(Il2CppObjectBase o)
        {
            var klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
            return Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(klass));
        }

        static string Describe(Il2CppObjectBase o)
        {
            var od = o.TryCast<Game.ObjectData>();
            if (od != null) return RuntimeClass(o) + "(" + od.id + ")";
            return RuntimeClass(o);
        }

        // Only properties backed by a real il2cpp field: computed game properties can have side effects.
        static IEnumerable<PropertyInfo> FieldProps(Type t)
        {
            var chain = new List<Type>();
            for (var c = t; c != null && c != typeof(Il2CppObjectBase) && c != typeof(UnityEngine.Object)
                 && c != typeof(ScriptableObject) && c != typeof(Il2CppSystem.Object); c = c.BaseType)
                chain.Add(c);
            chain.Reverse();
            foreach (var c in chain)
                foreach (var p in c.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.GetMethod == null || p.GetIndexParameters().Length != 0) continue;
                    if (c.GetField("NativeFieldInfoPtr_" + p.Name, BindingFlags.NonPublic | BindingFlags.Static) == null) continue;
                    yield return p;
                }
        }

        static void DumpFields(object obj, Type t, string indent, int depth)
        {
            foreach (var p in FieldProps(t))
            {
                object v;
                try { v = p.GetValue(obj); }
                catch (Exception e) { sb.AppendLine(indent + p.Name + " = <error " + e.GetType().Name + ">"); continue; }
                WriteValue(indent, p.Name, v, p.PropertyType, depth);
            }
        }

        static void WriteValue(string indent, string name, object v, Type type, int depth)
        {
            if (v == null) { sb.AppendLine(indent + name + " = null"); return; }
            switch (v)
            {
                case string s: sb.AppendLine(indent + name + " = \"" + s + "\""); return;
                case Vector2 v2: sb.AppendLine(indent + name + " = (" + v2.x + ", " + v2.y + ")"); return;
                case Vector3 v3: sb.AppendLine(indent + name + " = (" + v3.x + ", " + v3.y + ", " + v3.z + ")"); return;
                case Game.FloatRange fr: sb.AppendLine(indent + name + " = [" + fr.min + " .. " + fr.max + "]"); return;
                case AnimationCurve curve: sb.AppendLine(indent + name + " = curve " + CurveText(curve)); return;
                case Game.ObjectData od: sb.AppendLine(indent + name + " = " + Describe(od)); return;
            }
            if (type.IsPrimitive || type.IsEnum) { sb.AppendLine(indent + name + " = " + v); return; }
            if (v is UnityEngine.Object uo) { sb.AppendLine(indent + name + " = <" + RuntimeClass(uo) + ">"); return; }

            var count = type.GetProperty("Count") ?? type.GetProperty("Length");
            var indexer = type.GetProperty("Item");
            if (count != null && indexer != null)
            {
                int n = Convert.ToInt32(count.GetValue(v));
                var parts = new List<string>();
                var elemType = indexer.PropertyType;
                bool simple = true;
                for (int i = 0; i < n && i < 64; i++)
                {
                    var e = indexer.GetValue(v, new object[] { i });
                    if (e == null) parts.Add("null");
                    else if (e is string es) parts.Add("\"" + es + "\"");
                    else if (e is Game.ObjectData eod) parts.Add(Describe(eod));
                    else if (elemType.IsPrimitive || elemType.IsEnum) parts.Add(e.ToString());
                    else if (e is UnityEngine.Object) parts.Add("<" + RuntimeClass((Il2CppObjectBase)e) + ">");
                    else { simple = false; break; }
                }
                if (simple) { sb.AppendLine(indent + name + " = [" + string.Join(", ", parts) + "]"); return; }
                sb.AppendLine(indent + name + " = list x" + n);
                if (depth < 3)
                    for (int i = 0; i < n && i < 32; i++)
                    {
                        var e = indexer.GetValue(v, new object[] { i });
                        sb.AppendLine(indent + "  [" + i + "]");
                        if (e != null) DumpFields(e, elemType, indent + "    ", depth + 1);
                    }
                return;
            }

            if (v is Il2CppObjectBase && depth < 3)
            {
                sb.AppendLine(indent + name + ":");
                DumpFields(v, type, indent + "    ", depth + 1);
                return;
            }
            sb.AppendLine(indent + name + " = <" + type.Name + ">");
        }

        static string CurveText(AnimationCurve curve)
        {
            try
            {
                var keys = curve.keys;
                var parts = new List<string>();
                foreach (var k in keys) parts.Add("(" + k.time + " -> " + k.value + ")");
                return string.Join(" ", parts);
            }
            catch (Exception)
            {
                var parts = new List<string>();
                foreach (var t in new[] { 0f, 0.25f, 0.5f, 1f, 2f, 3f, 5f, 8f, 12f })
                    parts.Add("f(" + t + ")=" + curve.Evaluate(t));
                return "sampled " + string.Join(" ", parts);
            }
        }
    }

    unsafe class Disassembly
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte* buffer, IntPtr size, out IntPtr read);

        readonly BepInEx.Logging.ManualLogSource log;
        long modBase, modEnd;
        readonly Dictionary<long, string> methodByCode = new Dictionary<long, string>();
        readonly Dictionary<long, string> methodByInfo = new Dictionary<long, string>();
        readonly Dictionary<long, string> classByPtr = new Dictionary<long, string>();
        readonly Dictionary<string, IntPtr> classByName = new Dictionary<string, IntPtr>();
        readonly Dictionary<IntPtr, List<(string name, IntPtr info)>> methodsOfClass = new Dictionary<IntPtr, List<(string, IntPtr)>>();
        readonly List<string> stringLiterals = new List<string>();
        uint[] pdata;
        IntPtr stringClass;

        public Disassembly(BepInEx.Logging.ManualLogSource log) { this.log = log; }

        static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p);

        bool TryRead(long address, out long value)
        {
            long v = 0;
            bool ok = ReadProcessMemory(GetCurrentProcess(), (IntPtr)address, (byte*)&v, (IntPtr)8, out var read) && (long)read == 8;
            value = v;
            return ok;
        }

        public void Run()
        {
            var targetsFile = Path.Combine(ProbePlugin.OutDir, "targets.txt");
            if (!File.Exists(targetsFile)) { log.LogMessage("No probe\\targets.txt - skipping disassembly"); return; }
            var targets = File.ReadAllLines(targetsFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();

            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                { modBase = (long)m.BaseAddress; modEnd = modBase + m.ModuleMemorySize; }
            if (modBase == 0) throw new Exception("GameAssembly.dll not found");

            LoadPdata();
            LoadStringLiterals();
            IndexClasses();

            var asmDir = Path.Combine(ProbePlugin.OutDir, "asm");
            Directory.CreateDirectory(asmDir);
            var fields = new StringBuilder();
            var fieldClasses = new HashSet<string>();

            foreach (var target in targets)
            {
                if (target.StartsWith("fields "))
                {
                    fieldClasses.Add(target.Substring(7).Trim());
                    continue;
                }
                int sep = target.IndexOf("::", StringComparison.Ordinal);
                if (sep < 0) { log.LogWarning("Bad target: " + target); continue; }
                string cls = target.Substring(0, sep), meth = target.Substring(sep + 2);
                if (!classByName.TryGetValue(cls, out var klass)) { log.LogWarning("Class not found: " + cls); continue; }
                fieldClasses.Add(cls);
                var sb = new StringBuilder();
                int n = 0;
                foreach (var (name, info) in methodsOfClass[klass])
                {
                    if (meth != "*" && name != meth) continue;
                    long code = *(long*)info;
                    if (code == 0) continue;
                    sb.AppendLine("==== " + cls + "::" + name + "  rva 0x" + (code - modBase).ToString("X") + " params=" + IL2CPP.il2cpp_method_get_param_count(info));
                    DisassembleFunction(code, sb);
                    sb.AppendLine();
                    n++;
                }
                if (n == 0) { log.LogWarning("Method not found: " + target); continue; }
                string file = (cls + "." + meth).Replace('/', '+').Replace('*', '_').Replace('<', '_').Replace('>', '_') + ".asm";
                File.WriteAllText(Path.Combine(asmDir, file), sb.ToString());
            }

            foreach (var cls in fieldClasses.OrderBy(c => c))
            {
                if (!classByName.TryGetValue(cls, out var klass)) { fields.AppendLine("!! not found " + cls); continue; }
                fields.AppendLine("class " + cls + "  size 0x" + IL2CPP.il2cpp_class_instance_size(klass).ToString("X"));
                var rows = new List<(uint off, string text)>();
                for (var k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
                {
                    string owner = Utf8(IL2CPP.il2cpp_class_get_name(k));
                    if (owner == "Object" || owner == "MonoBehaviour" || owner == "Behaviour" || owner == "Component" || owner == "ScriptableObject") break;
                    IntPtr iter = IntPtr.Zero, f;
                    while ((f = IL2CPP.il2cpp_class_get_fields(k, ref iter)) != IntPtr.Zero)
                    {
                        bool isStatic = (IL2CPP.il2cpp_field_get_flags(f) & 0x10) != 0;
                        uint off = IL2CPP.il2cpp_field_get_offset(f);
                        string type = Utf8(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_field_get_type(f)));
                        rows.Add((isStatic ? off + 0x100000 : off, string.Format("  {0}0x{1:X3}  {2} {3}   [{4}]",
                            isStatic ? "static " : "", off, type, Utf8(IL2CPP.il2cpp_field_get_name(f)), owner)));
                    }
                }
                foreach (var r in rows.OrderBy(r => r.off)) fields.AppendLine(r.text);
                fields.AppendLine();
            }
            File.WriteAllText(Path.Combine(ProbePlugin.OutDir, "fields.txt"), fields.ToString());
            log.LogMessage("Probe disassembly complete: " + targets.Count + " targets");
        }

        void LoadPdata()
        {
            byte* b = (byte*)modBase;
            int pe = *(int*)(b + 0x3C);
            // PE32+ optional header starts at pe+24; data directories at +112; exception directory is index 3
            uint rva = *(uint*)(b + pe + 24 + 112 + 3 * 8);
            uint size = *(uint*)(b + pe + 24 + 112 + 3 * 8 + 4);
            pdata = new uint[size / 4];
            for (int i = 0; i < pdata.Length; i++) pdata[i] = *(uint*)(b + rva + i * 4);
        }

        long FunctionEnd(long code)
        {
            uint rva = (uint)(code - modBase);
            int lo = 0, hi = pdata.Length / 3 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                uint begin = pdata[mid * 3], end = pdata[mid * 3 + 1];
                if (rva < begin) hi = mid - 1;
                else if (rva >= end) lo = mid + 1;
                else
                {
                    // MSVC splits a function into several entries; the follow-ons carry UNW_FLAG_CHAININFO.
                    int n = pdata.Length / 3;
                    while (mid + 1 < n && pdata[(mid + 1) * 3] == end
                           && ((*(byte*)(modBase + pdata[(mid + 1) * 3 + 2]) >> 3) & 0x4) != 0)
                    {
                        mid++;
                        end = pdata[mid * 3 + 1];
                    }
                    return modBase + end;
                }
            }
            return 0;
        }

        void LoadStringLiterals()
        {
            var path = Path.Combine(Paths.GameRootPath, "castnchill_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            var d = File.ReadAllBytes(path);
            uint litOff = BitConverter.ToUInt32(d, 8), litSize = BitConverter.ToUInt32(d, 12), dataOff = BitConverter.ToUInt32(d, 16);
            for (uint o = litOff; o < litOff + litSize; o += 8)
                stringLiterals.Add(Encoding.UTF8.GetString(d, (int)(dataOff + BitConverter.ToUInt32(d, (int)o + 4)), (int)BitConverter.ToUInt32(d, (int)o)));
        }

        string ClassFullName(IntPtr klass)
        {
            var decl = IL2CPP.il2cpp_class_get_declaring_type(klass);
            string name = Utf8(IL2CPP.il2cpp_class_get_name(klass));
            if (decl != IntPtr.Zero) return ClassFullName(decl) + "/" + name;
            string ns = Utf8(IL2CPP.il2cpp_class_get_namespace(klass));
            return ns.Length > 0 ? ns + "." + name : name;
        }

        void IndexClasses()
        {
            uint count = 0;
            IntPtr* assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
            for (uint a = 0; a < count; a++)
            {
                IntPtr image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
                string imageName = Utf8(IL2CPP.il2cpp_image_get_name(image));
                bool wanted = imageName == "Game.Runtime.dll" || imageName == "mscorlib.dll" || imageName.StartsWith("UnityEngine.")
                              || imageName == "Unity.InputSystem.dll" || imageName == "FMODUnity.dll" || imageName == "DOTween.dll";
                if (!wanted) continue;
                bool isGame = imageName == "Game.Runtime.dll";
                uint classCount = IL2CPP.il2cpp_image_get_class_count(image);
                for (uint i = 0; i < classCount; i++)
                {
                    IntPtr klass = IL2CPP.il2cpp_image_get_class(image, i);
                    if (klass == IntPtr.Zero) continue;
                    string full = ClassFullName(klass);
                    classByPtr[(long)klass] = full;
                    if (isGame || !classByName.ContainsKey(full)) classByName[full] = klass;
                    if (full == "System.String") stringClass = klass;
                    var list = new List<(string, IntPtr)>();
                    methodsOfClass[klass] = list;
                    IntPtr iter = IntPtr.Zero, m;
                    while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                    {
                        string mname = Utf8(IL2CPP.il2cpp_method_get_name(m));
                        list.Add((mname, m));
                        string label = full + "::" + mname;
                        methodByInfo[(long)m] = label;
                        long code = *(long*)m;
                        if (code != 0 && !methodByCode.ContainsKey(code)) methodByCode[code] = label;
                    }
                }
            }
            log.LogInfo("Indexed " + classByPtr.Count + " classes, " + methodByCode.Count + " method bodies");
        }

        sealed class MemReader : CodeReader
        {
            byte* p; readonly byte* end;
            public MemReader(byte* start, byte* end) { p = start; this.end = end; }
            public override int ReadByte() => p < end ? *p++ : -1;
        }

        static readonly HashSet<Mnemonic> FloatOps = new HashSet<Mnemonic>
        {
            Mnemonic.Movss, Mnemonic.Mulss, Mnemonic.Addss, Mnemonic.Subss, Mnemonic.Divss, Mnemonic.Comiss, Mnemonic.Ucomiss,
            Mnemonic.Maxss, Mnemonic.Minss, Mnemonic.Cvtss2sd, Mnemonic.Sqrtss, Mnemonic.Movaps, Mnemonic.Movups,
            Mnemonic.Andps, Mnemonic.Xorps, Mnemonic.Mulps, Mnemonic.Addps, Mnemonic.Cmpss
        };

        string Annotate(in Instruction ins)
        {
            if (ins.Op0Kind == OpKind.NearBranch64 && (ins.FlowControl == FlowControl.Call || ins.FlowControl == FlowControl.UnconditionalBranch))
            {
                long t = (long)ins.NearBranchTarget;
                if (methodByCode.TryGetValue(t, out var name)) return name;
                if (ins.FlowControl == FlowControl.Call) return "sub_" + (t - modBase).ToString("X");
                return null;
            }
            if (!ins.IsIPRelativeMemoryOperand) return null;
            long addr = (long)ins.IPRelativeMemoryAddress;
            if (!TryRead(addr, out long value)) return null;
            if (FloatOps.Contains(ins.Mnemonic))
                return "float " + BitConverter.Int32BitsToSingle((int)value).ToString("R");
            if (ins.Mnemonic == Mnemonic.Movsd || ins.Mnemonic == Mnemonic.Mulsd || ins.Mnemonic == Mnemonic.Addsd
                || ins.Mnemonic == Mnemonic.Comisd || ins.Mnemonic == Mnemonic.Divsd || ins.Mnemonic == Mnemonic.Subsd)
                return "double " + BitConverter.Int64BitsToDouble(value).ToString("R");
            if (classByPtr.TryGetValue(value, out var cls)) return "class " + cls;
            if (methodByInfo.TryGetValue(value, out var mi)) return "methodinfo " + mi;
            if (methodByCode.TryGetValue(value, out var mc)) return "fnptr " + mc;
            if ((value & 1) == 1 && (ulong)value <= 0xFFFFFFFF)
            {
                uint enc = (uint)value, type = enc >> 29, index = (enc & 0x1FFFFFFE) >> 1;
                if (type == 5 && index < stringLiterals.Count) return "str \"" + stringLiterals[(int)index] + "\"";
                string[] kinds = { "?", "typeinfo", "il2cpptype", "methoddef", "fieldinfo", "strlit", "methodref", "fieldrva" };
                return "usage " + kinds[type & 7] + " #" + index;
            }
            if (value != 0 && (value < modBase || value >= modEnd) && stringClass != IntPtr.Zero
                && TryRead(value, out long k) && k == (long)stringClass && TryRead(value + 0x10, out long lenBits))
            {
                int len = (int)lenBits;
                if (len >= 0 && len < 200) return "str \"" + new string((char*)(value + 0x14), 0, len) + "\"";
            }
            return "[0x" + value.ToString("X") + "]";
        }

        void DisassembleFunction(long code, StringBuilder sb)
        {
            long end = FunctionEnd(code);
            bool leaf = end == 0; // no unwind info: stop at the first return
            if (leaf) end = code + 0x400;
            if (end - code > 0x6000) end = code + 0x6000;
            var decoder = Iced.Intel.Decoder.Create(64, new MemReader((byte*)code, (byte*)end));
            decoder.IP = (ulong)code;
            var formatter = new IntelFormatter();
            formatter.Options.HexPrefix = "0x";
            formatter.Options.HexSuffix = null;
            formatter.Options.SpaceAfterOperandSeparator = true;
            formatter.Options.RipRelativeAddresses = true;
            formatter.Options.BranchLeadingZeros = false;
            var output = new StringOutput();
            long maxTarget = 0;
            while ((long)decoder.IP < end)
            {
                decoder.Decode(out var ins);
                if (ins.IsInvalid) { sb.AppendLine("  <invalid>"); break; }
                formatter.Format(ins, output);
                string text = output.ToStringAndReset();
                string note = Annotate(ins);
                sb.Append("  ").Append(((long)ins.IP - modBase).ToString("X7")).Append("  ").Append(text);
                if (note != null) sb.Append("    ; ").Append(note);
                sb.AppendLine();
                if (ins.FlowControl == FlowControl.ConditionalBranch || ins.FlowControl == FlowControl.UnconditionalBranch)
                    if (ins.Op0Kind == OpKind.NearBranch64) maxTarget = Math.Max(maxTarget, (long)ins.NearBranchTarget);
                if (leaf && (long)decoder.IP > maxTarget
                    && (ins.FlowControl == FlowControl.Return || ins.FlowControl == FlowControl.UnconditionalBranch
                        || ins.FlowControl == FlowControl.IndirectBranch))
                    break;
            }
        }
    }
}
