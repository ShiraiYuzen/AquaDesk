// Polyfill：net48 缺失以下编译器依赖类型，而 init 访问器 / record / required 成员需要它们。
// 补上空实现即可让代码在 net48 下编译通过（运行时这些特性由 C# 编译器保证，不需要真实实现）。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Field | System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : System.Attribute
    {
    }

    [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : System.Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }
}
