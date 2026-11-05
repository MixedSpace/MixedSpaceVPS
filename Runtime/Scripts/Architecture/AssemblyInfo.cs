using System.Runtime.CompilerServices;

// 仅向测试和 SDK 编辑器程序集开放内部实现，运行时公共接口不因此扩展。
// Expose internals only to SDK editor and test assemblies; this does not extend the public runtime API.
[assembly: InternalsVisibleTo("MixedSpace.Core.Tests")]
[assembly: InternalsVisibleTo("MixedSpace.Core.Editor")]
