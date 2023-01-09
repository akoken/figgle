﻿// Copyright Drew Noakes. Licensed under the Apache-2.0 license. See the LICENSE file for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Figgle.Generator;

[Generator]
public sealed class FiggleSourceGenerator : ISourceGenerator
{
    public static readonly DiagnosticDescriptor UnknownFontNameDiagnostic = new(
        "FGL0001",
        "Unknown font name",
        "A font with name '{0}' was not found",
        category: "Figgle",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMemberNameDiagnostic = new(
        "FGL0002",
        "Invalid member name",
        "The string '{0}' is not a valid member name",
        category: "Figgle",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateMemberNameDiagnostic = new(
        "FGL0003",
        "Duplicate member name",
        "Member '{0}' has already been declared",
        category: "Figgle",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TypeIsNotPartialDiagnostic = new(
        "FGL0004",
        "Type must be partial",
        "Type '{0}' must be partial",
        category: "Figgle",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NestedTypeIsNotSupportedDiagnostic = new(
        "FGL0005",
        "Figgle generation does not support nested types",
        "Unable to generate Figgle text for nested type '{0}'. Generation is only supported for non-nested types.",
        category: "Figgle",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public const string AttributeSource =
        """
        // <auto-generated/>
        // Copyright Drew Noakes. Licensed under the Apache-2.0 license. See the LICENSE file for more details.

        using System;
        using System.Diagnostics;
        using System.Diagnostics.CodeAnalysis;

        namespace Figgle
        {
            [Conditional("INCLUDE_FIGGLE_GENERATOR_ATTRIBUTES")]
            [ExcludeFromCodeCoverage]
            [DebuggerNonUserCode]
            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            internal sealed class GenerateFiggleTextAttribute : Attribute
            {
                public string MemberName { get; }
                public string FontName { get; }
                public string SourceText { get; }

                public GenerateFiggleTextAttribute(string memberName, string fontName, string sourceText)
                {
                    MemberName = memberName;
                    FontName = fontName;
                    SourceText = sourceText;
                }
            }
        }
        """;

    public void Initialize(GeneratorInitializationContext context)
    {
        context.RegisterForSyntaxNotifications(() => new Receiver());
    }

    public void Execute(GeneratorExecutionContext context)
    {
        context.AddSource("GenerateFiggleTextAttribute.cs", AttributeSource);

        if (context.SyntaxContextReceiver is Receiver receiver)
        {
            foreach (var diagnostic in receiver.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }

            foreach (var pair in receiver.DataByType)
            {
                var type = pair.Key;
                var data = pair.Value;

                var sb = new StringBuilder();

                if (type.Namespace is not null)
                {
                    sb.AppendLine(
                        $$"""
                        namespace {{type.Namespace}}
                        {
                        """);
                }

                sb.AppendLine(
                    $$"""
                        partial class {{type.Class}}
                        {
                    """);

                foreach (var item in data.Items)
                {
                    var font = FiggleFonts.TryGetByName(item.FontName);

                    if (font is null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnknownFontNameDiagnostic,
                            item.FontNameLocation,
                            item.FontName));
                        continue;
                    }

                    if (!data.SeenMemberNames.Add(item.MemberName))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DuplicateMemberNameDiagnostic,
                            item.MemberNameLocation,
                            item.MemberName));
                        continue;
                    }

                    var text = font.Render(item.SourceText);

                    sb.AppendLine(
                        $$"""
                                public static string {{item.MemberName}} { get; } = @"{{text.Replace("\"", "\"\"")}}";
                        """);
                }

                sb.AppendLine(
                    """
                        }
                    """);

                if (type.Namespace is not null)
                {
                    sb.Append('}');
                }

                var hintName = type.Namespace is null
                    ? $"{type.Class}.FiggleGenerated.cs"
                    : $"{type.Namespace}.{type.Class}.FiggleGenerated.cs";

                context.AddSource(hintName, sb.ToString());
            }
        }
    }

    private record TypeItems(List<RenderItem> Items, HashSet<string> SeenMemberNames);

    private record RenderItem(string MemberName, Location MemberNameLocation, string FontName, Location FontNameLocation, string SourceText);

    private sealed class Receiver : ISyntaxContextReceiver
    {
        public List<Diagnostic> Diagnostics { get; } = new();

        public Dictionary<(string Class, string? Namespace), TypeItems> DataByType { get; } = new();

        public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
        {
            if (context.Node is ClassDeclarationSyntax classDeclaration)
            {
                foreach (var attributeList in classDeclaration.AttributeLists)
                {
                    foreach (var attribute in attributeList.Attributes)
                    {
                        if (attribute.Name is IdentifierNameSyntax name &&
                            (name.Identifier.Text == "GenerateFiggleTextAttribute" || name.Identifier.Text == "GenerateFiggleText") &&
                            attribute.ArgumentList is not null &&
                            attribute.ArgumentList.Arguments.Count == 3 &&
                            attribute.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax arg0 &&
                            attribute.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax arg1 &&
                            attribute.ArgumentList.Arguments[2].Expression is LiteralExpressionSyntax arg2)
                        {
                            var classSymbol = ModelExtensions.GetDeclaredSymbol(context.SemanticModel, classDeclaration);

                            if (classSymbol is not INamedTypeSymbol namedTypeSymbol)
                            {
                                throw new("Unable to obtain source class details.");
                            }

                            (string ClassName, string? Namespace) key = (namedTypeSymbol.Name, namedTypeSymbol.ContainingNamespace.IsGlobalNamespace ? null : namedTypeSymbol.ContainingNamespace.ToString());

                            if (namedTypeSymbol.ContainingType is not null)
                            {
                                Diagnostics.Add(Diagnostic.Create(
                                    NestedTypeIsNotSupportedDiagnostic,
                                    Location.Create(attribute.SyntaxTree, attribute.Span),
                                    key.ClassName));
                                continue;
                            }

                            if (!classDeclaration.Modifiers.Any(modifier => modifier.Text == "partial"))
                            {
                                Diagnostics.Add(Diagnostic.Create(
                                    TypeIsNotPartialDiagnostic,
                                    Location.Create(attribute.SyntaxTree, attribute.Span),
                                    key.ClassName));
                                continue;
                            }

                            if (!DataByType.TryGetValue(key, out var typeItems))
                            {
                                DataByType[key] = typeItems = new(new(), new(StringComparer.Ordinal));
                            }

                            typeItems.SeenMemberNames.UnionWith(namedTypeSymbol.MemberNames);

                            var memberName = arg0.Token.ValueText;
                            var memberNameLocation = ToLocation(attribute.ArgumentList.Arguments[0]);

                            var fontName = arg1.Token.ValueText;
                            var fontNameLocation = ToLocation(attribute.ArgumentList.Arguments[1]);

                            var sourceText = arg2.Token.ValueText;

                            if (!SyntaxFacts.IsValidIdentifier(memberName))
                            {
                                Diagnostics.Add(Diagnostic.Create(
                                    InvalidMemberNameDiagnostic,
                                    memberNameLocation,
                                    memberName));
                                continue;
                            }

                            typeItems.Items.Add(new(memberName, memberNameLocation, fontName, fontNameLocation, sourceText));

                            static Location ToLocation(SyntaxNode node) => Location.Create(node.SyntaxTree, node.Span);
                        }
                    }
                }
            }
        }
    }
}
