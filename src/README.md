```markdown
# SqlInjectionAnalyzer (démo Roslyn analyzer)

Ce projet est un démonstrateur minimal d'un Roslyn Analyzer qui signale les cas où une chaîne construite par interpolation ($"...{x}...") ou par concaténation ("a" + b) est directement utilisée comme SQL dans des "sinks" fréquents :
- constructeur/CommandText de SqlCommand
- EF Core ExecuteSqlRaw / FromSqlRaw / ExecuteSqlInterpolated
- Dapper Query / Execute
- méthodes courantes d'IDbConnection / DbCommand (ExecuteNonQuery / ExecuteReader / ExecuteScalar)

Objectif : signaler les usages dangereux et proposer une aide (CodeFix basique).

Important : c'est une solution heuristique — elle peut produire des faux positifs et ne remplace pas une vraie analyse de flux (taint-analysis) complète. Voir la section "Améliorations" plus bas.

## Structure fournie
- src/SqlInjectionAnalyzer : projet Analyzer + CodeFixProvider
  - SqlInjectionAnalyzer.cs : analyzer principal
  - SqlInjectionCodeFixProvider.cs : CodeFix (ajoute un commentaire TODO pour guider la correction)
  - README.md : ce fichier (en français)

## Comment compiler et tester localement
1. Prérequis : SDK .NET 6 (ou supérieur) installé.
2. Ouvrir un terminal dans le dossier racine contenant `src/SqlInjectionAnalyzer`.
3. Compiler :
   dotnet build src/SqlInjectionAnalyzer/SqlInjectionAnalyzer.csproj
4. Pour utiliser cet analyzer localement dans un autre projet :
   - Ajoutez le projet à la même solution et ajoutez une référence de projet au projet que vous voulez analyser, OU
   - Créez un paquet NuGet et ajoutez-le en tant que `PackageReference`.
5. Exemple : dans votre projet cible (ex : WebApi) ajoutez dans le csproj :
   ```xml
   <ItemGroup>
     <ProjectReference Include="..\<path>\src\SqlInjectionAnalyzer\SqlInjectionAnalyzer.csproj" />
   </ItemGroup>
   ```
   Puis `dotnet build` : les diagnostics apparaîtront dans l'IDE / à la compilation.

## Intégration CI
- Ajoutez `dotnet build` à votre pipeline (GitHub Actions, Azure Pipelines).
- Optionnel : dans une `.editorconfig` vous pouvez promouvoir ce diagnostic (SI1001) en erreur pour bloquer les builds :
  ```
  dotnet_diagnostic.SI1001.severity = error
  ```

## Limitations connues
- Heuristique simple basée sur IOperation :
  - détecte interpolations et concaténations utilisées directement dans les sinks ;
  - peut produire des faux positifs (ex. SQL construit à partir de constantes ou via un utilitaire qui paramètre ensuite) ;
- Ne réalise pas de suivi complet des données ("taint analysis") à travers plusieurs méthodes, champs, retours, etc.
- Le CodeFix n'effectue pas une paramétrisation automatique complète (trop risqué génériquement) — il insère un commentaire TODO explicite. Vous pouvez implémenter des CodeFixs plus avancés pour des motifs spécifiques.

## Améliorations / étapes suivantes recommandées
1. Ajouter une analyse de flux (taint) :
   - Utiliser `DataFlowAnalysis` (ControlFlowAnalysis / DataFlowAnalysis) pour propager l'état "tainted" depuis les paramètres d'API vers les sinks.
   - Mettre en place une propagation interprocédurale pour suivre les valeurs à travers les méthodes.
2. Affiner la détection des sinks :
   - Résoudre les types pleins (System.Data.SqlClient.SqlCommand, Microsoft.Data.SqlClient.SqlCommand, Dapper.SqlMapper).
   - Détecter les appels d'extension (DatabaseFacade.ExecuteSqlRaw).
3. Améliorer le CodeFix :
   - Générer automatiquement un paramètre et remplacer l'interpolation par `@ParamName`, puis ajouter `cmd.Parameters.AddWithValue("@ParamName", value)` pour des patterns simples (implémentation délicate mais possible).
4. Compléter avec des tests unitaires Roslyn (Microsoft.CodeAnalysis.Testing) pour valider analyzers et fixes.

## Exemple d'utilisation typique
Avant :
```csharp
var sql = $"SELECT * FROM Users WHERE name = '{userName}'";
conn.Query(sql);
```
L'analyzer signale SI1001 sur la chaîne interpolée.

Après (recommandé) :
```csharp
var sql = "SELECT * FROM Users WHERE name = @Name";
conn.Query(sql, new { Name = userName });
```

---

Si vous le souhaitez, je peux maintenant :
- générer des tests unitaires d'exemple pour prouver le diagnostic ;
- étendre l'analyzer pour couvrir davantage de motifs (EF/Dapper) ;
- implémenter un CodeFix plus automatique pour des motifs simples (p. ex. Dapper ou ADO.NET basique).