#r "C:/Users/tobia/.nuget/packages/com.ivanmurzak.mcpplugin/7.2.0/lib/net8.0/McpPlugin.dll"
#r "C:/Users/tobia/.nuget/packages/com.ivanmurzak.reflectornet/5.3.2/lib/net8.0/ReflectorNet.dll"

using System.Reflection;
var asm = Assembly.LoadFrom(@"C:\Users\tobia\.nuget\packages\com.ivanmurzak.mcpplugin\7.2.0\lib\net8.0\McpPlugin.dll");
foreach (var t in asm.GetExportedTypes().OrderBy(x => x.FullName))
    Console.WriteLine($"{t.FullName}  (base: {t.BaseType?.Name})");
