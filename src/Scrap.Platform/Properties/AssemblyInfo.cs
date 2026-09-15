using System.Runtime.CompilerServices;

// 仅向系统边界测试开放内部的 endpoint 命名 seam。
// Expose the internal endpoint-naming seam only to system-boundary tests.
[assembly: InternalsVisibleTo("Scrap.Integration.Tests")]
