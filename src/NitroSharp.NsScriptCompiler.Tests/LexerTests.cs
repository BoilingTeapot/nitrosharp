﻿using NitroSharp.NsScriptNew;
using NitroSharp.NsScriptNew.Syntax;
using NitroSharp.NsScriptNew.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NitroSharp.NsScriptCompiler.Tests
{
    public class LexerTests
    {
        [Theory]
        [InlineData("\"foo", DiagnosticId.UnterminatedString, 0, 0)]
        [InlineData("<PRE box00", DiagnosticId.UnterminatedDialogueBlockStartTag, 0, 0)]
        [InlineData("/* multiline comment", DiagnosticId.UnterminatedComment, 0, 0)]
        [InlineData("[text001", DiagnosticId.UnterminatedDialogueBlockIdentifier, 0, 0, LexingMode.DialogueBlock)]
        [InlineData("2147483648", DiagnosticId.NumberTooLarge, 0, 10)]
        public void Lexer_Emits_Diagnostics(
            string text, DiagnosticId diagnosticId,
            int spanStart, int spanEnd, LexingMode lexingMode = LexingMode.Normal)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text, lexingMode);
            var diagnostic = Assert.Single(ctx.Diagnostics.All);
            Assert.Equal(diagnosticId, diagnostic.Id);
            Assert.Equal(TextSpan.FromBounds(spanStart, spanEnd), diagnostic.Span);
        }

        [Theory]
        [MemberData(nameof(GetTokenData))]
        public void Lexer_Recognizes_Static_Token(SyntaxTokenKind kind, string text)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text);
            Assert.Equal(kind, token.Kind);
            Assert.Equal(text, SyntaxFacts.GetText(kind));
            Assert.Equal(text, ctx.GetText(token).ToString(), ignoreCase: true);
            Assert.Equal(SyntaxTokenFlags.Empty, token.Flags);
        }

        [Theory]
        [MemberData(nameof(GetTokenPairData))]
        public void Lexer_Handles_Token_Pair(SyntaxTokenKind t1Kind, string t1Text, SyntaxTokenKind t2Kind, string t2Text)
        {
            string text = t1Text + t2Text;
            (SyntaxTokenEnumerable tkEnumerable, LexingContext ctx) = Parsing.LexTokens(text);
            var tokens = tkEnumerable.ToArray();
            Assert.Equal(3, tokens.Length);
            Assert.Equal(tokens[0].Kind, t1Kind);
            Assert.Equal(ctx.GetText(tokens[0]).ToString(), t1Text, ignoreCase: true);
            Assert.Equal(tokens[1].Kind, t2Kind);
            Assert.Equal(ctx.GetText(tokens[1]).ToString(), t2Text, ignoreCase: true);
        }

        [Theory]
        [MemberData(nameof(GetTokenPairsWithSeparatorData))]
        public void Lexer_Handles_Token_Pair_With_Separator(
            SyntaxTokenKind t1Kind, string t1Text,
            string separator,
            SyntaxTokenKind t2Kind, string t2Text)
        {
            string text = t1Text + separator + t2Text;
            (SyntaxTokenEnumerable tkEnumerable, LexingContext ctx) = Parsing.LexTokens(text);
            var tokens = tkEnumerable.ToArray();
            Assert.Equal(3, tokens.Length);
            Assert.Equal(tokens[0].Kind, t1Kind);
            Assert.Equal(ctx.GetText(tokens[0]).ToString(), t1Text, ignoreCase: true);
            Assert.Equal(tokens[1].Kind, t2Kind);
            Assert.Equal(ctx.GetText(tokens[1]).ToString(), t2Text, ignoreCase: true);
        }

        [Theory]
        [InlineData("42", SyntaxTokenKind.NumericLiteral, "42")]
        [InlineData("42.2", SyntaxTokenKind.NumericLiteral, "42.2", SyntaxTokenFlags.HasDecimalPoint)]
        [InlineData("#FFFFFF", SyntaxTokenKind.NumericLiteral, "FFFFFF", SyntaxTokenFlags.IsHexTriplet)]
        [InlineData("\"foo\"", SyntaxTokenKind.StringLiteral, "foo", SyntaxTokenFlags.IsQuoted)]
        public void Lexer_Recognizes_Literals(string text, SyntaxTokenKind tokenKind, string valueText,
            SyntaxTokenFlags flags = SyntaxTokenFlags.Empty)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text);
            Assert.Equal(tokenKind, token.Kind);
            Assert.Equal(text, ctx.GetText(token).ToString());
            Assert.Equal(valueText, ctx.GetValueText(token).ToString());
            Assert.Equal(flags, token.Flags);
        }

        [Theory]
        [InlineData("foo", "foo", SyntaxTokenFlags.Empty)]
        [InlineData("$foo", "foo", SyntaxTokenFlags.HasDollarPrefix)]
        [InlineData("#foo", "foo", SyntaxTokenFlags.HasHashPrefix)]
        [InlineData("@foo", "foo", SyntaxTokenFlags.HasAtPrefix)]
        public void Lexer_Recognizes_Identifiers(string text, string valueText, SyntaxTokenFlags flags)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text);
            Assert.Equal(SyntaxTokenKind.Identifier, token.Kind);
            Assert.Equal(text, ctx.GetText(token).ToString());
            Assert.Equal(valueText, ctx.GetValueText(token).ToString());
            Assert.Equal(flags, token.Flags);
        }

        [Theory]
        [InlineData("\"$foo\"", "foo", SyntaxTokenFlags.IsQuoted | SyntaxTokenFlags.HasDollarPrefix)]
        [InlineData("\"#foo\"", "foo", SyntaxTokenFlags.IsQuoted | SyntaxTokenFlags.HasHashPrefix)]
        [InlineData("\"@foo\"", "foo", SyntaxTokenFlags.IsQuoted | SyntaxTokenFlags.HasAtPrefix)]
        public void Lexer_Recognizes_StringLiteralOrQuotedIdentifier_Token(string text, string valueText, SyntaxTokenFlags flags)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text);
            Assert.Equal(SyntaxTokenKind.StringLiteralOrQuotedIdentifier, token.Kind);
            Assert.Equal(text, ctx.GetText(token).ToString());
            Assert.Equal(valueText, ctx.GetValueText(token).ToString());
            Assert.Equal(flags, token.Flags);
        }

        [Theory]
        [InlineData(SyntaxTokenKind.TrueKeyword)]
        [InlineData(SyntaxTokenKind.FalseKeyword)]
        [InlineData(SyntaxTokenKind.NullKeyword)]
        public void Lexer_Recognizes_Certain_Quoted_Keywords(SyntaxTokenKind keyword)
        {
            string quotedText = $"\"{SyntaxFacts.GetText(keyword)}\"";
            (SyntaxToken token, LexingContext ctx) = LexToken(quotedText);
            Assert.Equal(keyword, token.Kind);
            Assert.Equal(SyntaxTokenFlags.IsQuoted, token.Flags);
        }

        [Theory]
        [InlineData("True", SyntaxTokenKind.TrueKeyword)]
        [InlineData("False", SyntaxTokenKind.FalseKeyword)]
        [InlineData("Null", SyntaxTokenKind.NullKeyword)]
        public void Lexer_Recognizes_Certain_PascalCase_Keywords(string text, SyntaxTokenKind keyword)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text);
            Assert.Equal(keyword, token.Kind);
        }

        [Theory]
        [InlineData("TRUE", SyntaxTokenKind.TrueKeyword)]
        [InlineData("FALSE", SyntaxTokenKind.FalseKeyword)]
        [InlineData("NULL", SyntaxTokenKind.NullKeyword)]
        public void Lexer_Recognizes_Certain_UpperCase_Keywords(string text, SyntaxTokenKind keyword)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text);
            Assert.Equal(keyword, token.Kind);
        }

        [Fact]
        public void Identifier_Cannot_Start_With_Dot()
        {
            (SyntaxTokenEnumerable tkEnumerable, LexingContext ctx) = Parsing.LexTokens("$.");
            var tokens = tkEnumerable.ToArray();
            Assert.Equal(3, tokens.Length);
            Assert.Equal(SyntaxTokenKind.Dollar, tokens[0].Kind);
            Assert.Equal(SyntaxTokenKind.Dot, tokens[1].Kind);
        }

        [Theory]
        [InlineData("[text001]", SyntaxTokenKind.DialogueBlockIdentifier)]
        [InlineData("\r", SyntaxTokenKind.PXmlLineSeparator)]
        [InlineData("\n", SyntaxTokenKind.PXmlLineSeparator)]
        [InlineData("foo", SyntaxTokenKind.PXmlString)]
        public void Lexer_Recognizes_Dynamic_PXml_Token(string text, SyntaxTokenKind kind)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text, LexingMode.DialogueBlock);
            Assert.Equal(kind, token.Kind);
            Assert.Equal(text, ctx.GetText(token).ToString());
            Assert.Equal(SyntaxTokenFlags.Empty, token.Flags);
        }

        [Theory]
        [InlineData("<PRE box00>", SyntaxTokenKind.DialogueBlockStartTag)]
        [InlineData("<pre box00>", SyntaxTokenKind.DialogueBlockStartTag)]
        [InlineData("</PRE>", SyntaxTokenKind.DialogueBlockEndTag)]
        [InlineData("</pre>", SyntaxTokenKind.DialogueBlockEndTag)]
        public void Lexer_Recognizes_Dialogue_Block_Tags(string text, SyntaxTokenKind kind)
        {
            (SyntaxToken token, LexingContext ctx) = LexToken(text);
            Assert.Equal(kind, token.Kind);
            Assert.Equal(text, ctx.GetText(token).ToString());
            Assert.Equal(SyntaxTokenFlags.Empty, token.Flags);
        }

        private static (SyntaxToken token, LexingContext ctx) LexToken(string text, LexingMode mode = LexingMode.Normal)
        {
            SyntaxToken tk = default;
            (SyntaxTokenEnumerable tokens, LexingContext ctx) = Parsing.LexTokens(text, mode);
            foreach (SyntaxToken token in tokens)
            {
                if (tk.Kind == SyntaxTokenKind.None)
                {
                    tk = token;
                }
                else if (token.Kind != SyntaxTokenKind.EndOfFileToken)
                {
                    Assert.True(false, "More than one token was lexed.");
                }
            }

            if (tk.Kind == SyntaxTokenKind.None)
            {
                Assert.True(false, "No tokens were lexed.");
            }

            return (tk, ctx);
        }

        public static IEnumerable<object[]> GetTokenData()
        {
            foreach (var token in GetTokens())
            {
                if (token.kind != SyntaxTokenKind.PXmlLineSeparator)
                {
                    yield return new object[] { token.kind, token.text };
                }
            }
        }

        public static IEnumerable<object[]> GetTokenPairData()
        {
            foreach (var pair in GetTokenPairs())
            {
                yield return new object[] { pair.t1Kind, pair.t1Text, pair.t2Kind, pair.t2Text };
            }
        }

        public static IEnumerable<object[]> GetTokenPairsWithSeparatorData()
        {
            foreach (var pair in GetTokenPairsWithSeparator())
            {
                yield return new object[] { pair.t1Kind, pair.t1Text, pair.separatorText, pair.t2Kind, pair.t2Text };
            }
        }

        private static IEnumerable<(SyntaxTokenKind kind, string text)> GetTokens()
        {
            var fixedTokens = Enum.GetValues(typeof(SyntaxTokenKind))
                                  .Cast<SyntaxTokenKind>()
                                  .Select(k => (kind: k, text: SyntaxFacts.GetText(k)))
                                  .Where(t => !string.IsNullOrEmpty(t.text));

            return fixedTokens;
        }

        private static IEnumerable<(SyntaxTokenKind t1Kind, string t1Text, SyntaxTokenKind t2Kind, string t2Text)> GetTokenPairs()
        {
            foreach (var tk1 in GetTokens())
            {
                if (tk1.kind == SyntaxTokenKind.PXmlLineSeparator) continue;
                foreach (var tk2 in GetTokens())
                {
                    if (tk2.kind == SyntaxTokenKind.PXmlLineSeparator) continue;
                    if (!RequireSeparator(tk1.kind, tk2.kind))
                    {
                        yield return (tk1.kind, tk1.text, tk2.kind, tk2.text);
                    }
                }
            }
        }

        private static IEnumerable<(SyntaxTokenKind t1Kind, string t1Text,
                                    string separatorText,
                                    SyntaxTokenKind t2Kind, string t2Text)> GetTokenPairsWithSeparator()
        {
            foreach (var tk1 in GetTokens())
            {
                if (tk1.kind == SyntaxTokenKind.PXmlLineSeparator) continue;
                foreach (var tk2 in GetTokens())
                {
                    if (tk2.kind == SyntaxTokenKind.PXmlLineSeparator) continue;
                    if (RequireSeparator(tk1.kind, tk2.kind))
                    {
                        foreach (string separator in GetSeparators())
                        {
                            yield return (tk1.kind, tk1.text, separator, tk2.kind, tk2.text);
                        }
                    }
                }
            }
        }

        private static string[] GetSeparators()
        {
            return new[]
            {
                " ",
                "\t",
                "\r",
                "\n",
                "\r\n",
            };
        }

        private static bool RequireSeparator(SyntaxTokenKind kind1, SyntaxTokenKind kind2)
        {
            bool isKeyword(SyntaxTokenKind kind) =>
                (int)kind >= (int)SyntaxTokenKind.ChapterKeyword
                && (int)kind <= (int)SyntaxTokenKind.ReturnKeyword;

            bool isIdentifierOrKeyword(SyntaxTokenKind kind) =>
                isKeyword(kind)
                || kind == SyntaxTokenKind.Identifier;

            bool canFollowKeyword(SyntaxTokenKind kind)
                => !isKeyword(kind)
                && SyntaxFacts.IsIdentifierStopCharacter(SyntaxFacts.GetText(kind)[0]);

            bool isSigil(SyntaxTokenKind kind)
                => kind == SyntaxTokenKind.Dollar
                || kind == SyntaxTokenKind.Hash;

            bool canFormCompountPunctuation(SyntaxTokenKind kind)
            {
                switch (kind1)
                {
                    case SyntaxTokenKind.Equals:
                    case SyntaxTokenKind.Minus:
                    case SyntaxTokenKind.Plus:
                    case SyntaxTokenKind.Asterisk:
                    case SyntaxTokenKind.Slash:
                    case SyntaxTokenKind.LessThan:
                    case SyntaxTokenKind.GreaterThan:
                    case SyntaxTokenKind.Exclamation:
                    case SyntaxTokenKind.Ampersand:
                        return true;

                    default:
                        return false;
                }
            }

            if (isIdentifierOrKeyword(kind1) && isSigil(kind2)) return true;
            if (isIdentifierOrKeyword(kind2) && isSigil(kind1)) return true;

            bool tk1IsKeyword = isKeyword(kind1);
            bool tk2IsKeyword = isKeyword(kind2);

            if (tk1IsKeyword)
            {
                return !canFollowKeyword(kind2);
            }
            if (tk2IsKeyword)
            {
                return !canFollowKeyword(kind1);
            }

            if (canFormCompountPunctuation(kind1) && canFormCompountPunctuation(kind2))
            {
                return true;
            }

            // <dot><dot>
            if (kind1 == SyntaxTokenKind.Dot && kind2 == SyntaxTokenKind.Dot)
            {
                return true;
            }

            return false;
        }

        public static IEnumerable<object[]> GetKeywordData()
        {
            foreach (var kind in EnumerateKeywords())
            {
                yield return new object[] { kind, SyntaxFacts.GetText(kind) };
            }
        }

        private static IEnumerable<SyntaxTokenKind> EnumerateKeywords()
        {
            int start = (int)SyntaxTokenKind.ChapterKeyword;
            int end = (int)SyntaxTokenKind.ReturnKeyword;
            for (int kind = start; kind <= end; kind *= 2)
            {
                yield return (SyntaxTokenKind)kind;
            }
        }
    }
}
