using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SqlInjectionAnalyzer
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SqlInjectionCodeFixProvider)), Shared]
    public class SqlInjectionCodeFixProvider : CodeFixProvider
    {
        private const string Title = "Paramétrer la requête SQL / Éviter l'interpolation de chaîne";

        public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(SqlInjectionAnalyzer.DiagnosticId);

        public sealed override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) return;

            var diagnostic = context.Diagnostics[0];
            var span = diagnostic.Location.SourceSpan;
            var node = root.FindNode(span);

            // Nous fournissons une correction simple et prudente : insérer un commentaire TODO au-dessus de l'instruction
            // recommandant la paramétration. La transformation automatique complète est trop spécifique au contexte.
            context.RegisterCodeFix(
                Microsoft.CodeAnalysis.CodeActions.CodeAction.Create(
                    title: Title,
                    createChangedDocument: c => AddTodoCommentAsync(context.Document, node, c),
                    equivalenceKey: Title),
                diagnostic);
        }

        private async Task<Document> AddTodoCommentAsync(Document document, SyntaxNode node, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null) return document;

            var trivia = SyntaxFactory.Comment("// TODO : Cette requête SQL est construite par interpolation/concaténation — paramétrez la requête pour éviter une injection SQL (utilisez SqlParameter, paramètres Dapper ou la paramétrisation EF)." );
            var leading = node.GetLeadingTrivia().Insert(0, trivia).Insert(1, SyntaxFactory.EndOfLine("\n"));

            var newNode = node.WithLeadingTrivia(leading);
            var newRoot = root.ReplaceNode(node, newNode);
            return document.WithSyntaxRoot(newRoot);
        }
    }
}