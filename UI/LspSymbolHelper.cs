namespace DotNetIDE;

internal static class LspSymbolHelper
{
    public static string GetSymbolKindName(int kind) => kind switch
    {
        1 => "File", 2 => "Module", 3 => "Namespace", 4 => "Package",
        5 => "Class", 6 => "Method", 7 => "Property", 8 => "Field",
        9 => "Constructor", 10 => "Enum", 11 => "Interface", 12 => "Function",
        13 => "Variable", 14 => "Constant", 15 => "String", 16 => "Number",
        17 => "Boolean", 18 => "Array", 19 => "Object", 22 => "Struct",
        23 => "Event", 24 => "Operator", 25 => "TypeParam",
        _ => "Symbol"
    };
}
