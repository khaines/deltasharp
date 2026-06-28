using DeltaSharp;

// Minimal getting-started sample. It confirms that a .NET 8 (current LTS) application can
// reference the public DeltaSharp.Core surface and call into it. The M1 skeleton is
// intentionally inert and exposes only build/version metadata; richer SparkSession and
// DataFrame samples arrive with the public API in EPIC-04.
//
// `System` is available via ImplicitUsings (Directory.Build.props), so `Console` needs no
// explicit using.
Console.WriteLine($"{DeltaSharpInfo.Product} {DeltaSharpInfo.Version}");
