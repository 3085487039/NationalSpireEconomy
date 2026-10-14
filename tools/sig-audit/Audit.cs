// 上游 API 签名核对（阶段 5 §3.1）—— 纯反射，只读，不执行任何上游代码。
using System;
using System.Linq;
using System.Reflection;

public static class Audit
{
    static string Sig(MethodBase m)
    {
        string ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
        string ret = (m is MethodInfo mi) ? mi.ReturnType.Name : "void";
        return m.Name + "(" + ps + ") -> " + ret;
    }

    static string Tok(MethodBase m)
    {
        try { return "0x" + m.MetadataToken.ToString("X8"); }
        catch { return "(dynamic)"; }
    }

    public static void Report(string dll)
    {
        Assembly asm = Assembly.LoadFrom(dll);
        Console.WriteLine("程序集: " + asm.FullName);
        Console.WriteLine();

        var wanted = new (string TypeName, string Member, string Hint)[] {
            ("NationalSpire.AiService",              "RequestPrivateAsync", "方案A主挂载点(prefix)"),
            ("NationalSpire.AiService",              "ProcessPrivateAsync", "身份跟踪(MoveNext)"),
            ("NationalSpire.CareerEngine",           "MoveToNextDay",       "日期推进(postfix,首选)"),
            ("NationalSpire.CareerEngine",           "AdvanceToDay",        "日期推进(后备)"),
            ("NationalSpire.PrivateStreamParser",    "Feed",                "流式抽取(prefix)"),
            ("NationalSpire.PrivateStreamParser",    "Finish",              "流式收尾(prefix)"),
            ("NationalSpire.PrivateMessagePrompts",  "Compose",             "旧Compose补丁(已退场)"),
            ("NationalSpire.PrivateMessagePrompts",  "Context",             "上下文(诊断)"),
            ("NationalSpire.LeadingReasoningStream", "Feed",                "并行可见流(prefix)"),
            ("NationalSpire.LeadingReasoningStream", "Finish",              "并行可见流收尾"),
        };

        foreach (var w in wanted)
        {
            Type t = asm.GetType(w.TypeName, false);
            if (t == null)
            {
                Console.WriteLine("[缺失类型] " + w.TypeName + "   <== 整个类型都不在了！  (" + w.Hint + ")");
                continue;
            }

            MethodBase[] all = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static)
                                .Where(m => m.Name == w.Member)
                                .Cast<MethodBase>().ToArray();

            if (all.Length == 0)
            {
                Console.WriteLine("[缺失方法] " + w.TypeName + "." + w.Member + "   <== 方法名变了！  (" + w.Hint + ")");
                continue;
            }

            foreach (var m in all)
                Console.WriteLine("[找到] " + w.TypeName + "." + Sig(m) + "  token=" + Tok(m) + "  (" + w.Hint + ")");
        }

        Console.WriteLine();
        Console.WriteLine("---- 相关类型是否存在 ----");
        foreach (string tn in new[] {
            "NationalSpire.AiService", "NationalSpire.CareerEngine",
            "NationalSpire.PrivateStreamParser", "NationalSpire.PrivateMessagePrompts",
            "NationalSpire.LeadingReasoningStream", "NationalSpire.PrivateTurn",
            "NationalSpire.CareerData", "NationalSpire.Diagnostics" })
        {
            Type t = asm.GetType(tn, false);
            Console.WriteLine((t != null ? "[有] " : "[无] ") + tn);
        }
    }
}
