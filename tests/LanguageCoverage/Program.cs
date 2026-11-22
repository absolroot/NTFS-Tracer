using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var gui = args is [var path] ? path : throw new ArgumentException("Pass the path to the gui source directory.");
var resource = XDocument.Load(Path.Combine(gui, "JapaneseMessages.resx"), LoadOptions.PreserveWhitespace);
var messages = resource.Root!.Elements("data").ToDictionary(row => (string)row.Attribute("name")!, row => (string)row.Element("value")!, StringComparer.Ordinal);
var keys = new HashSet<string>(StringComparer.Ordinal);
var formatItem = new Regex(@"(?<!\{)\{\d+[^{}]*\}(?!\})", RegexOptions.CultureInvariant);

foreach (string file in Directory.EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories))
{
    if (Path.GetRelativePath(gui, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
        continue;
    var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
    foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        if (call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "L" }, Name.Identifier.ValueText: "T" })
            keys.UnionWith(EnglishKeys(call.ArgumentList.Arguments[1].Expression));
}

foreach (string key in keys)
{
    if (!messages.TryGetValue(key, out string? translated) || string.IsNullOrWhiteSpace(translated))
        throw new InvalidDataException($"Missing Japanese translation: {key}");
    CompositeFormat.Parse(key);
    CompositeFormat.Parse(translated);
    var expected = formatItem.Matches(key).Select(match => match.Value).Order(StringComparer.Ordinal);
    var actual = formatItem.Matches(translated).Select(match => match.Value).Order(StringComparer.Ordinal);
    if (!expected.SequenceEqual(actual))
        throw new InvalidDataException($"Format items differ: {key} -> {translated}");
}

Console.WriteLine($"Validated {keys.Count} Japanese message templates, including plural forms and switch keys.");

static IEnumerable<string> EnglishKeys(ExpressionSyntax expression)
{
    if (expression is LiteralExpressionSyntax literal && literal.Token.Value is string text)
        return [text];
    if (expression is ConditionalExpressionSyntax conditional)
        return EnglishKeys(conditional.WhenTrue).Concat(EnglishKeys(conditional.WhenFalse));
    if (expression is IdentifierNameSyntax id
        && expression.FirstAncestorOrSelf<SwitchExpressionArmSyntax>() is { Pattern: ConstantPatternSyntax { Expression: var pattern }, Parent: SwitchExpressionSyntax owner }
        && owner.GoverningExpression is IdentifierNameSyntax argument && argument.Identifier.ValueText == id.Identifier.ValueText)
        return EnglishKeys(pattern);
    throw new InvalidDataException($"Unrecognized translation key at {expression.GetLocation().GetLineSpan()}: {expression}");
}

