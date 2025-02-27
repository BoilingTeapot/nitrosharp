﻿using System.Globalization;

namespace NitroSharp.NsScript
{
    public enum DiagnosticId
    {
        UnterminatedString,
        UnterminatedQuotedIdentifier,
        UnterminatedComment,
        UnterminatedDialogueBlockStartTag,
        UnterminatedDialogueBlockIdentifier,
        NumberTooLarge,

        TokenExpected,
        StrayToken,
        MisplacedSemicolon,
        ExpectedSubroutineDeclaration,
        MissingStatementTerminator,
        InvalidExpressionTerm,
        InvalidExpressionStatement,
        StrayMarkupBlock,
        MisplacedBreak,
        OrphanedSelectSection,
        InvalidBezierCurve,

        UnresolvedIdentifier,
        BadAssignmentTarget,
        ExternalModuleNotFound,
        ChapterMainNotFound
    }

    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public class Diagnostic
    {
        public static Diagnostic Create(TextSpan span, DiagnosticId id) => new(span, id);
        public static Diagnostic Create(TextSpan span, DiagnosticId id, params object[] arguments)
            => new DiagnosticWithArguments(span, id, arguments);

        private Diagnostic(TextSpan span, DiagnosticId id)
        {
            Span = span;
            Id = id;
        }

        public DiagnosticId Id { get; }
        public TextSpan Span { get; }
        public virtual string Message => DiagnosticInfo.GetMessage(Id);
        public DiagnosticSeverity Severity => DiagnosticInfo.GetSeverity(Id);

        private sealed class DiagnosticWithArguments : Diagnostic
        {
            private readonly object[] _arguments;
            private string? _message;

            public DiagnosticWithArguments(TextSpan span, DiagnosticId id, params object[] arguments)
                : base(span, id)
            {
                _arguments = arguments;
            }

            public override string Message => _message ??= FormatMessage();

            private string FormatMessage()
            {
                string formatString = DiagnosticInfo.GetMessage(Id);
                return string.Format(CultureInfo.CurrentCulture, formatString, _arguments);
            }
        }
    }
}
