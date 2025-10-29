using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SqlInjectionAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class SqlInjectionAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SI1001";
        private static readonly LocalizableString Title = "Possible injection SQL : chaîne SQL construite à partir d'une entrée non-constante";
        private static readonly LocalizableString MessageFormat = "La chaîne SQL passée ici est construite par concaténation ou interpolation de chaînes ; paramétrez la requête au lieu d'insérer directement des données utilisateur.";
        private static readonly LocalizableString Description = "Détecte les chaînes interpolées et les concaténations de chaînes passées à des sinks SQL courants (SqlCommand.CommandText, constructeur SqlCommand, EF Core ExecuteSqlRaw/FromSqlRaw, Dapper Query/Execute, etc.).";
        private const string Category = "Sécurité";

        private static DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: Description);

        // Noms de méthodes sink connus (heuristique)
        private static readonly ImmutableHashSet<string> SinkMethodNames = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "ExecuteSqlRaw", "ExecuteSqlInterpolated", "FromSqlRaw", "FromSqlInterpolated", "Query", "QueryAsync",
            "Execute", "ExecuteAsync", "ExecuteReader", "ExecuteNonQuery", "ExecuteScalar", "ExecuteSqlCommand", "SqlQuery");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            // Configuration pour les performances
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            // Enregistrer les actions d'opération pour les chaînes interpolées et les opérations binaires (concaténation)
            context.RegisterOperationAction(AnalyzeInterpolatedString, OperationKind.InterpolatedString);
            context.RegisterOperationAction(AnalyzeBinary, OperationKind.BinaryOperator);
        }

        private static void AnalyzeInterpolatedString(OperationAnalysisContext context)
        {
            var interp = (IInterpolatedStringOperation)context.Operation;
            // Si la chaîne contient des interpolations (c.-à-d. des expressions), on la considère dynamique
            if (!ContainsNonEmptyInterpolation(interp)) return;

            // Remonter l'arbre pour vérifier si cette chaîne est utilisée dans un sink SQL
            if (IsUsedInSqlSink(interp, context.Compilation, context.ContainingSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, interp.Syntax.GetLocation()));
            }
        }

        private static void AnalyzeBinary(OperationAnalysisContext context)
        {
            var bin = (IBinaryOperation)context.Operation;
            // Seulement intéresser par la concaténation de chaînes (opérateur +)
            if (bin.OperatorKind != BinaryOperatorKind.Addition) return;
            if (bin.Type == null || bin.Type.SpecialType != SpecialType.System_String) return;

            // Si l'une des deux parties n'est pas une constante, on suppose que la chaîne est dynamique
            if (!IsPossiblyDynamicString(bin)) return;

            if (IsUsedInSqlSink(bin, context.Compilation, context.ContainingSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, bin.Syntax.GetLocation()));
            }
        }

        private static bool ContainsNonEmptyInterpolation(IInterpolatedStringOperation interp)
        {
            // Si une partie est une expression (pas seulement du texte), la chaîne est dynamique
            return interp.Parts.Any(p => p.Kind == Microsoft.CodeAnalysis.Operations.OperationKind.Interpolation);
        }

        private static bool IsPossiblyDynamicString(IBinaryOperation bin)
        {
            // Si l'un des opérandes n'est pas une chaîne littérale constante, considérer dynamique
            bool leftConst = IsConstantString(bin.LeftOperand);
            bool rightConst = IsConstantString(bin.RightOperand);
            return !(leftConst && rightConst);
        }

        private static bool IsConstantString(IOperation op)
        {
            return op.ConstantValue.HasValue && op.ConstantValue.Value is string;
        }

        private static bool IsUsedInSqlSink(IOperation stringOp, Compilation compilation, ISymbol containingSymbol)
        {
            // Remonter les parents jusqu'à une profondeur limitée et inspecter l'utilisation
            IOperation? current = stringOp;
            int depth = 0;
            while (current != null && depth++ < 10)
            {
                // Argument d'un appel de méthode ?
                if (current.Parent is IArgumentOperation argOp)
                {
                    var invocation = argOp.Parent as IInvocationOperation;
                    if (invocation != null)
                    {
                        var method = invocation.TargetMethod;
                        if (IsSqlSinkMethod(method))
                            return true;

                        // Heuristique : vérifier le nom du type contenant la méthode
                        if (IsTypeLikelySqlApi(method.ContainingType))
                            return true;
                    }
                }

                // Assignation à une propriété / champ ?
                if (current.Parent is ISimpleAssignmentOperation assignOp)
                {
                    // propriété à gauche ?
                    var left = assignOp.Target;
                    if (left is IPropertyReferenceOperation propRef)
                    {
                        var propName = propRef.Property.Name;
                        if (string.Equals(propName, "CommandText", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                // Création d'objet, par ex. new SqlCommand(sql, conn)
                if (current.Parent is IObjectCreationOperation objCreation)
                {
                    var ctor = objCreation.Constructor;
                    if (ctor != null && ctor.Parameters.Length > 0)
                    {
                        // si notre chaîne est un argument du constructeur
                        if (objCreation.Arguments.Any(a => a.Value == current))
                        {
                            if (IsTypeLikelySqlApi(ctor.ContainingType))
                                return true;
                        }
                    }
                }

                current = current.Parent;
            }

            return false;
        }

        private static bool IsSqlSinkMethod(IMethodSymbol? method)
        {
            if (method == null) return false;
            if (SinkMethodNames.Contains(method.Name)) return true;
            // Heuristique : méthode appartenant à Dapper.SqlMapper, IDbConnection, DbCommand, DatabaseFacade...
            var containing = method.ContainingType;
            if (containing == null) return false;

            var tname = containing.Name ?? "";
            if (tname.IndexOf("SqlMapper", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tname.IndexOf("DbCommand", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tname.IndexOf("DbConnection", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tname.IndexOf("DatabaseFacade", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tname.IndexOf("SqlCommand", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tname.IndexOf("IDbConnection", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private static bool IsTypeLikelySqlApi(INamedTypeSymbol? type)
        {
            if (type == null) return false;
            var name = type.Name ?? "";
            if (name.IndexOf("SqlCommand", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("SqlConnection", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("DbCommand", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("DbConnection", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("DatabaseFacade", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}