using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using System.Reflection;

// 语法树格式化，不用正则表达式猜测 C# 大括号。检查模式不改源码，错误返回非零。
string root = Path.GetFullPath(args[0]);
bool check = args.Contains("--check");
int changed = 0;
using var workspace = new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies.Add(Assembly.Load("Microsoft.CodeAnalysis.CSharp.Workspaces"))));
var options = workspace.Options
    .WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\r\n")
    .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, false)
    .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, 4)
    .WithChangedOption(CSharpFormattingOptions.WrappingPreserveSingleLine, false)
    .WithChangedOption(CSharpFormattingOptions.WrappingKeepStatementsOnSingleLine, false);
var files = new[]
{
    "src",
    "tests",
    "build-support"
}.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)).Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
foreach (string file in files)
{
    string original = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(original, new CSharpParseOptions(LanguageVersion.Preview));
    if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
    {
        throw new InvalidDataException("C# syntax error: " + file);
    }

    var source = tree.GetRoot();
    var expanded = new ControlBlockRewriter().Visit(source)!;
    expanded = new LongListRewriter().Visit(expanded)!;
    // 重新建立位置映射，再让 Roslyn 计算新增换行的缩进。
    expanded = CSharpSyntaxTree.ParseText(expanded.ToFullString()).GetRoot();
    var normalized = Formatter.Format(ClosingParentheses.Compact(expanded), workspace, options);
    normalized = ContinuationIndentation.Apply(normalized);
    normalized = Formatter.Format(normalized, workspace, options);
    var spaced = new MemberSpacingRewriter().Visit(normalized)!;
    string formatted = spaced.ToFullString().TrimEnd() + "\r\n";
    var parsed = CSharpSyntaxTree.ParseText(formatted, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
    if (parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
    {
        throw new InvalidDataException("Formatter produced invalid syntax: " + file);
    }

    // 唯一允许增加的语法 token 是控制语句外的大括号；字符串、运算符和标识符保持原样。
    static IEnumerable<(int, string)> Tokens(SyntaxNode node) => node.DescendantTokens().Where(t => !t.IsKind(SyntaxKind.OpenBraceToken) && !t.IsKind(SyntaxKind.CloseBraceToken)).Select(t => (t.RawKind, t.Text));
    if (!Tokens(source).SequenceEqual(Tokens(parsed)))
    {
        throw new InvalidDataException("Formatter changed non-brace tokens: " + file);
    }

    if (original == formatted)
    {
        continue;
    }

    changed++;
    Console.WriteLine((check ? "FORMAT: " : "Formatted: ") + Path.GetRelativePath(root, file));
    if (!check)
    {
        File.WriteAllText(file, formatted, new UTF8Encoding(false));
    }
}

Console.WriteLine($"C# layout: {changed} file(s) {(check ? "need formatting" : "updated")}.");
return check && changed > 0 ? 1 : 0;

/// <summary>数组或 lambda 的右花括号与调用右括号相邻；保留中间任何注释。</summary>
static class ClosingParentheses
{
    public static SyntaxNode Compact(SyntaxNode root)
    {
        var replacements = new Dictionary<SyntaxToken, SyntaxToken>();
        foreach (var token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.CloseParenToken) || t.IsKind(SyntaxKind.CloseBracketToken)))
        {
            var previous = token.GetPreviousToken();
            if (previous.IsKind(SyntaxKind.CloseBraceToken) && previous.TrailingTrivia.Concat(token.LeadingTrivia)
                .All(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia)))
            {
                replacements[previous] = previous.WithTrailingTrivia();
                replacements[token] = token.WithLeadingTrivia();
            }
        }
        return root.ReplaceTokens(replacements.Keys, (original, _) => replacements[original]);
    }
}

/// <summary>参数续行统一缩进一级；Roslyn 默认保留第一个参数的已有对齐位置。</summary>
static class ContinuationIndentation
{
    public static SyntaxNode Apply(SyntaxNode root)
    {
        var text = root.SyntaxTree.GetText();
        var indentation = new Dictionary<int, int>();
        int Width(int line)
        {
            return indentation.TryGetValue(line, out int width)
                ? width
                : text.Lines[line].ToString().TakeWhile(char.IsWhiteSpace).Count();
        }

        // 外层参数表先处理，内层调用可以沿用本轮已经计算出的行缩进。
        foreach (var node in root.DescendantNodes().Where(n => n is ArgumentListSyntax or ParameterListSyntax))
        {
            var items = node is ArgumentListSyntax arguments
                ? arguments.Arguments.Cast<SyntaxNode>()
                : ((ParameterListSyntax)node).Parameters.Cast<SyntaxNode>();
            int openingLine = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber;
            foreach (var item in items)
            {
                int position = item.GetFirstToken().SpanStart;
                var line = text.Lines.GetLineFromPosition(position);
                if (line.LineNumber > openingLine && text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(line.Start, position)).All(char.IsWhiteSpace))
                {
                    indentation[line.LineNumber] = Width(openingLine) + 4;
                }
            }
        }

        var changes = indentation.Select(pair =>
        {
            var line = text.Lines[pair.Key];
            int oldWidth = line.ToString().TakeWhile(char.IsWhiteSpace).Count();
            return new Microsoft.CodeAnalysis.Text.TextChange(new(line.Start, oldWidth), new string(' ', pair.Value));
        });
        return CSharpSyntaxTree.ParseText(text.WithChanges(changes), new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
    }
}

/// <summary>给嵌入语句补大括号；else if 保留链式结构，不改变分支归属。</summary>
sealed class ControlBlockRewriter : CSharpSyntaxRewriter
{
    private static StatementSyntax Block(StatementSyntax statement) => statement is BlockSyntax ? statement : SyntaxFactory.Block(statement);

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var result = (IfStatementSyntax)base.VisitIfStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitElseClause(ElseClauseSyntax node)
    {
        var result = (ElseClauseSyntax)base.VisitElseClause(node)!;
        return result.Statement is IfStatementSyntax ? result : result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var result = (ForStatementSyntax)base.VisitForStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var result = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var result = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var result = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var result = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        var result = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }

    public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
    {
        var result = (LockStatementSyntax)base.VisitLockStatement(node)!;
        return result.WithStatement(Block(result.Statement));
    }
}

/// <summary>方法、属性和类型之间保留一个空行；注释与属性标记仍紧贴所属成员。</summary>
sealed class MemberSpacingRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var result = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
        if (result.Members.Count > 0)
        {
            var first = result.Members[0];
            var previous = result.SemicolonToken.TrailingTrivia;
            int breaks = previous.Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) + first.GetLeadingTrivia()
                .TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia))
                .Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            if (breaks < 2)
            {
                var spaced = first.WithLeadingTrivia(Enumerable.Repeat(SyntaxFactory.EndOfLine("\r\n"), 2 - breaks).Concat(first.GetLeadingTrivia()));
                result = result.WithMembers(result.Members.Replace(first, spaced));
            }
        }
        return result.WithMembers(Space(result.Members));
    }

    private static SyntaxList<MemberDeclarationSyntax> Space(SyntaxList<MemberDeclarationSyntax> members)
    {
        for (int i = 1; i < members.Count; i++)
        {
            if (members[i - 1] is FieldDeclarationSyntax && members[i] is FieldDeclarationSyntax)
            {
                continue;
            }

            int breaks = members[i - 1].GetTrailingTrivia().Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            breaks += members[i].GetLeadingTrivia().TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia)).Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            if (breaks < 2)
            {
                var prefix = Enumerable.Repeat(SyntaxFactory.EndOfLine("\r\n"), 2 - breaks);
                members = members.Replace(members[i], members[i].WithLeadingTrivia(prefix.Concat(members[i].GetLeadingTrivia())));
            }
        }

        return members;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var result = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        return result.WithMembers(Space(result.Members));
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var result = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
        return result.WithMembers(Space(result.Members));
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var result = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        return result.WithMembers(Space(result.Members));
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        var result = (InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!;
        return result.WithMembers(Space(result.Members));
    }
}

/// <summary>展开较长参数表和对象初始化；只在语法分隔符处换行，不触碰字符串内容。</summary>
sealed class LongListRewriter : CSharpSyntaxRewriter
{
    private static SeparatedSyntaxList<T> Split<T>(SeparatedSyntaxList<T> items) where T : SyntaxNode =>
        SyntaxFactory.SeparatedList(
            items,
            items.GetSeparators().Select(token => token.WithTrailingTrivia(token.TrailingTrivia.Add(SyntaxFactory.ElasticCarriageReturnLineFeed))));

    public override SyntaxNode? VisitArgumentList(ArgumentListSyntax node)
    {
        var result = (ArgumentListSyntax)base.VisitArgumentList(node)!;
        if (result.Arguments.Count < 2 || result.ToString().Contains('\n') || result.ToString().Length < 120)
        {
            return result;
        }

        return result.WithOpenParenToken(result.OpenParenToken.WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed))
                    .WithArguments(Split(result.Arguments));
    }

    public override SyntaxNode? VisitParameterList(ParameterListSyntax node)
    {
        var result = (ParameterListSyntax)base.VisitParameterList(node)!;
        if (result.Parameters.Count < 2 || result.ToString().Contains('\n') || result.ToString().Length < 100)
        {
            return result;
        }

        return result.WithOpenParenToken(result.OpenParenToken.WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed))
                    .WithParameters(Split(result.Parameters));
    }

    public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
    {
        var result = (InitializerExpressionSyntax)base.VisitInitializerExpression(node)!;
        if (result.Expressions.Count < 2 || result.ToString().Contains('\n') || result.ToString().Length < 100)
        {
            return result;
        }

        return result.WithOpenBraceToken(result.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed))
                    .WithExpressions(Split(result.Expressions))
                    .WithCloseBraceToken(result.CloseBraceToken.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));
    }
}
