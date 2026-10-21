using System;
using System.Reflection;
using System.Text;
public static class ConsoleDump
{
    public static string Run()
    {
        var t = Type.GetType("UnityEditor.LogEntries, UnityEditor");
        var sb = new StringBuilder();
        var count = (int)t.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
        sb.AppendLine("entries=" + count);
        var et = Type.GetType("UnityEditor.LogEntry, UnityEditor");
        var start = t.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
        var end = t.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);
        var get = t.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);
        var msg = et.GetField("message", BindingFlags.Instance | BindingFlags.Public);
        start.Invoke(null, null);
        try
        {
            for (int i = Math.Max(0, count - 12); i < count; i++)
            {
                var e = Activator.CreateInstance(et);
                get.Invoke(null, new object[] { i, e });
                var m = (string)msg.GetValue(e);
                if (m.Length > 300) m = m.Substring(0, 300);
                sb.AppendLine(i + ": " + m.Replace("\n", " | "));
            }
        }
        finally { end.Invoke(null, null); }
        return sb.ToString();
    }
}
