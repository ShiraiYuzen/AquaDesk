// ---------------------------------------------------------------------------
// net48 兼容垫片（仅用于 AquaDesk-net48 方案2 实验）。
// 这些类型/方法让 net8 源码能在 .NET Framework 4.8 上编译运行，
// 原 .NET 8 版 AquaDesk 不受影响。
// ---------------------------------------------------------------------------

namespace System
{
    // 让 ^ 和 .. 运算符在 net48 上可用（源码里只用于 string 切片，会降级成 string.Substring）。
    public readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        public static Index Start => FromStart(0);
        public static Index End => new Index(~0);

        public static Index FromStart(int value) => new Index(value);
        public static Index FromEnd(int value) => new Index(value, fromEnd: true);

        public int GetOffset(int length)
        {
            int offset = IsFromEnd ? _value + length + 1 : _value;
            return offset;
        }

        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object obj) => obj is Index other && Equals(other);
        public override int GetHashCode() => _value;

        public static implicit operator Index(int value) => FromStart(value);
    }

    public readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }

        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object obj) => obj is Range other && Equals(other);
        public override int GetHashCode() => Start.GetHashCode() * 397 ^ End.GetHashCode();
    }
}

// 全局命名空间：扩展方法与静态垫片，任意文件都能直接用到。
internal static class NetFxShims
{
    // 对应 net8 的 Math.Clamp（net48 没有）。提供常用数值重载。
    public static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
    public static double Clamp(double value, double min, double max) => value < min ? min : (value > max ? max : value);
    public static float Clamp(float value, float min, float max) => value < min ? min : (value > max ? max : value);
    public static long Clamp(long value, long min, long max) => value < min ? min : (value > max ? max : value);

    // 对应 net8 的 NetFxShims.ProcessPath。
    public static string ProcessPath
    {
        get
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }
    }

    // 对应 net8 的 NetFxShims.ProcessId。
    public static int ProcessId => System.Diagnostics.Process.GetCurrentProcess().Id;

    // 对应 net8 的 NetFxShims.TickCount64（系统启动至今的毫秒数）。
    public static long TickCount64 => (uint)System.Environment.TickCount;

    // 对应 net8 的 NetFxShims.Move(src, dst, overwrite)（net48 没有 overwrite 重载）。
    public static void Move(string source, string dest, bool overwrite = false)
    {
        if (overwrite && System.IO.File.Exists(dest)) System.IO.File.Delete(dest);
        System.IO.File.Move(source, dest);
    }
}

internal static class NetFxExtensions
{
    // net8 的 string.Contains / StartsWith / EndsWith 单字符与带 comparison 重载（net48 缺）。
    public static bool Contains(this string source, string value, StringComparison comparison)
        => source.IndexOf(value, comparison) >= 0;

    public static bool Contains(this string source, char value)
        => source.IndexOf(value) >= 0;

    public static bool StartsWith(this string source, char value)
        => source.Length > 0 && source[0] == value;

    public static bool EndsWith(this string source, char value)
        => source.Length > 0 && source[source.Length - 1] == value;

    // net8 的 Task.IsCompletedSuccessfully（net48 没有，注意是方法不是属性，调用要加括号）。
    public static bool IsCompletedSuccessfully(this System.Threading.Tasks.Task task)
        => task.IsCompleted && !task.IsFaulted && !task.IsCanceled;

    // net8 的 KeyValuePair.Deconstruct（net48 缺），用于 foreach ((k, v) in dict)。
    public static void Deconstruct<TKey, TValue>(
        this System.Collections.Generic.KeyValuePair<TKey, TValue> kvp,
        out TKey key, out TValue value)
    {
        key = kvp.Key;
        value = kvp.Value;
    }
}
